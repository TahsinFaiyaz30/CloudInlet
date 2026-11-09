using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Packaging;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

/// <summary>Explicitly enrolled disposable guest only. Never part of ordinary desktop acceptance.</summary>
[TestClass]
[DoNotParallelize]
public sealed class GuestKnownFolderAcceptanceTests
{
    private const string LabRoot = @"C:\CloudInletMigrationLab";
    private const string FolderItem = "b-folders";
    private static readonly string[] Selected = ["Documents", "Pictures", "Music"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly byte[] IdentityEntropy = "CloudInlet.GuestAcceptance.Identity.v1"u8.ToArray();
    private static readonly byte[] BoundaryEntropy = "CloudInlet.GuestAcceptance.KnownFolders.v1"u8.ToArray();
    public TestContext TestContext { get; set; } = null!;

    private sealed record Enrollment(int SchemaVersion, Guid FixtureId, Guid VmId, Guid FirmwareUuid,
        string ComputerName, string ExpectedAccountSid, string AccountName, string ProfilePath, string LabRoot, bool DedicatedGuest);
    private sealed record BootEvidence(string FirmwareUuid, string Manufacturer, string Model, DateTimeOffset LastBootUtc,
        DateTimeOffset KernelBootEventUtc, long KernelBootRecordId);
    private sealed record Boundary(int Schema, Guid FixtureId, string EnrollmentSha256, string BootIdentity,
        BootEvidence Boot, string Phase, string Source, string Destination, string FixtureFile,
        Dictionary<string, string> OriginalPaths, Dictionary<string, string> PayloadSha256,
        Dictionary<string, string> RuntimeSha256, ClientFolderMigration.DirectoryIdentity? DirectoryIdentity, string PlanFingerprint);

    [TestMethod]
    [TestCategory("LiveProvider")]
    [TestCategory("GuestOnly")]
    [Timeout(180_000)]
    public async Task NativeGuestKnownFoldersRecoverAcrossAuthenticatedStages()
    {
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_GUEST_FOLDER_ACCEPTANCE") != "1")
            Assert.Inconclusive("Requires explicit enrollment and opt-in inside a dedicated disposable Hyper-V guest. No Shell writes were made.");
        var stage = Environment.GetEnvironmentVariable("CLOUDINLET_GUEST_FOLDER_ACCEPTANCE_STAGE");
        Assert.IsTrue(stage is "Prepare" or "VerifyBeforeRestart" or "VerifyAfterRestart" or "Revert" or "RestoreMappings" or "RevertPriorRuntime" or "RestorePriorRuntime",
            "Select one explicit guest acceptance stage.");
        var priorRuntimeRecovery = stage is "RevertPriorRuntime" or "RestorePriorRuntime";
        Assert.IsTrue(OperatingSystem.IsWindows());
        var guards = new List<SafeFileHandle>();
        try
        {
            // No directories, files or Shell state are created until independent guest checks pass.
            RequireOrdinaryPath(LabRoot);
            ClientRegistrationMigration.PinAncestors(LabRoot, guards);
            var identityBytes = ReadBounded(Path.Combine(LabRoot, "guest-identity.json"), 16 * 1024);
            var envelope = ProtectedData.Unprotect(ReadBounded(Path.Combine(LabRoot, "guest-identity.dpapi"), 32 * 1024),
                IdentityEntropy, DataProtectionScope.CurrentUser);
            Enrollment enrolled;
            try
            {
                CollectionAssert.AreEqual(identityBytes, envelope, "Enrollment changed after guest-account protection.");
                enrolled = JsonSerializer.Deserialize<Enrollment>(envelope, Json) ?? throw new InvalidDataException("Missing guest identity.");
            }
            finally { CryptographicOperations.ZeroMemory(envelope); }
            var boot = ReadBootEvidence();
            using var current = WindowsIdentity.GetCurrent();
            Assert.AreEqual(1, enrolled.SchemaVersion);
            Assert.IsTrue(enrolled.DedicatedGuest && enrolled.FixtureId != Guid.Empty && enrolled.VmId != Guid.Empty && enrolled.FirmwareUuid != Guid.Empty);
            Assert.AreEqual("Microsoft Corporation", boot.Manufacturer);
            Assert.AreEqual("Virtual Machine", boot.Model);
            Assert.AreEqual(enrolled.FirmwareUuid, Guid.Parse(boot.FirmwareUuid), "This is not the enrolled guest firmware identity.");
            Assert.AreEqual(enrolled.ExpectedAccountSid, current.User!.Value);
            Assert.AreEqual(enrolled.AccountName, current.Name, true);
            Assert.AreEqual(enrolled.ComputerName, Environment.MachineName, true);
            Assert.IsTrue(enrolled.AccountName.StartsWith(Environment.MachineName + "\\CloudInletLab", StringComparison.OrdinalIgnoreCase));
            Same(enrolled.LabRoot, LabRoot);
            Same(enrolled.ProfilePath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.IsTrue(enrolled.ProfilePath.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase));
            RequireOrdinaryPath(enrolled.ProfilePath);
            ClientRegistrationMigration.PinAncestors(enrolled.ProfilePath, guards);
            var root = Path.Combine(LabRoot, "known-folders-" + enrolled.FixtureId.ToString("N"));
            var scenarioText = Environment.GetEnvironmentVariable("CLOUDINLET_GUEST_FOLDER_SCENARIO_ID");
            if (!string.IsNullOrEmpty(scenarioText))
            {
                Assert.IsTrue(Guid.TryParseExact(scenarioText, "D", out var scenarioId) && scenarioId != Guid.Empty,
                    "A separate guest scenario must have an explicit nonempty GUID.");
                root += "-" + scenarioId.ToString("N");
            }
            var identityHash = Hash(identityBytes);
            var bootId = InstallerMigrationRecovery.CurrentBootIdentity();
            Assert.IsTrue(Guid.TryParse(bootId, out var parsedBoot) && parsedBoot != Guid.Empty);
            if (stage == "Prepare")
            {
                Assert.IsFalse(Directory.Exists(root) || File.Exists(root), "Preparation never replaces an existing fixture.");
                Directory.CreateDirectory(root);
            }
            else Assert.IsTrue(Directory.Exists(root), "Existing authenticated fixture evidence is required.");
            RequireOrdinaryPath(root);
            ClientRegistrationMigration.PinAncestors(root, guards);
            var reportPath = Path.Combine(root, "stage-" + stage + "-" + Guid.NewGuid().ToString("N") + ".json");
            var succeeded = false;
            string? failure = null;
            try
            {
                if (stage == "Prepare") await PrepareAsync(enrolled, root, identityHash, bootId, boot);
                else
                {
                    var boundary = Load(root);
                    Assert.AreEqual(enrolled.FixtureId, boundary.FixtureId);
                    Assert.AreEqual(identityHash, boundary.EnrollmentSha256, "Enrollment cannot change between stages.");
                    ValidateBoundary(root, boundary, priorRuntimeRecovery);
                    if (stage == "VerifyBeforeRestart")
                    {
                        Assert.AreEqual(boundary.BootIdentity, bootId, "Run the before-restart stage on the original boot.");
                        Assert.AreEqual(boundary.Boot.LastBootUtc, boot.LastBootUtc);
                        VerifyPartial(root, boundary);
                        Save(root, boundary with { Phase = "VerifiedBeforeRestart" });
                    }
                    else if (stage == "VerifyAfterRestart")
                    {
                        Assert.AreEqual("VerifiedBeforeRestart", boundary.Phase);
                        Assert.AreNotEqual(boundary.BootIdentity, bootId, "A real guest reboot is required; changing a receipt is not acceptance.");
                        Assert.IsTrue(boot.LastBootUtc > boundary.Boot.LastBootUtc);
                        Assert.IsTrue(boot.KernelBootRecordId > boundary.Boot.KernelBootRecordId && boot.KernelBootEventUtc > boundary.Boot.KernelBootEventUtc,
                            "Independent Windows Kernel-General boot event evidence must advance.");
                        Assert.IsTrue(Math.Abs((boot.KernelBootEventUtc - boot.LastBootUtc).TotalMinutes) < 3);
                        VerifyPartial(root, boundary, allowRecreatedFolder: true);
                        await RecoverAsync(root, boundary, revert: false);
                        Save(root, boundary with { Phase = "CompletedAfterRealRestart" });
                    }
                    else if (stage is "Revert" or "RevertPriorRuntime")
                    {
                        Assert.IsTrue(boundary.Phase is "Prepared" or "VerifiedBeforeRestart");
                        VerifyPartial(root, boundary, allowRecreatedFolder: true);
                        await RecoverAsync(root, boundary, revert: true);
                        Save(root, boundary with { Phase = "Reverted" });
                    }
                    else await RestoreAsync(root, boundary);
                }
                succeeded = true;
            }
            catch (Exception error) { failure = error.ToString(); throw; }
            finally
            {
                var report = new
                {
                    stage, succeeded, failure, enrolled.FixtureId, enrolled.VmId, enrolled.FirmwareUuid,
                    bootIdentity = bootId, bootEvidence = boot,
                    priorRuntimeRecovery, currentRuntimeSha256 = RuntimeHashes(),
                    priorRuntimeInventorySha256 = priorRuntimeRecovery ? Environment.GetEnvironmentVariable("CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY_SHA256") : null,
                    actualRebootVerified = succeeded && stage == "VerifyAfterRestart",
                    nativeInstaller3010Exercised = false, winUiObserverExercised = false,
                    hostStateImported = false, fixturesDeleted = false,
                    timeUtc = DateTimeOffset.UtcNow
                };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
                TestContext.AddResultFile(reportPath);
            }
        }
        finally { foreach (var guard in guards) guard.Dispose(); }
    }

    private static async Task PrepareAsync(Enrollment enrolled, string root, string identityHash, string bootId, BootEvidence boot)
    {
        var source = Path.Combine(root, BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay");
        var destination = Path.Combine(root, BuildInfo.Flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet");
        var file = "CloudInlet-acceptance-" + enrolled.FixtureId.ToString("N") + ".txt";
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Selected.Append("Downloads"))
        {
            var path = KnownFolderBackup.GetPath(name);
            Same(path, KnownFolderBackup.GetDefaultPath(name));
            Assert.IsTrue(path.StartsWith(enrolled.ProfilePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            RequireOrdinaryPath(path);
            if (Selected.Contains(name)) GuestKnownFolderPreflight.Validate(path);
            Assert.IsFalse(File.Exists(Path.Combine(path, file)) || Directory.Exists(Path.Combine(path, file)));
            original.Add(name, path);
            hashes.Add(name, Hash(Payload(enrolled.FixtureId, name)));
        }
        var plan = Plan(root, source, destination);
        var boundary = new Boundary(1, enrolled.FixtureId, identityHash, bootId, boot, "Preparing", source,
            destination, file, original, hashes, RuntimeHashes(), null, plan.Fingerprint);
        Save(root, boundary); // Ownership evidence precedes fixture creation or the first actual Shell write.
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Records(root));
        var backups = new List<BackupFolder>();
        foreach (var name in Selected.Append("Downloads"))
        {
            Directory.CreateDirectory(original[name]);
            using (var output = new FileStream(Path.Combine(original[name], file), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(Payload(enrolled.FixtureId, name)); output.Flush(true); }
            if (!Selected.Contains(name)) continue;
            var review = KnownFolderBackup.Preview(name, source, null, BackupTransferMode.Copy, true);
            backups.Add(await KnownFolderBackup.EnableReviewedAsync(review, default));
            Same(Path.Combine(source, name), KnownFolderBackup.GetPath(name));
            CheckPayload(boundary, name, Path.Combine(source, name));
        }
        var storage = new ClientStorage(State(root));
        storage.SaveSettings(new AppSettings
        {
            RootPath = source, Prefix = "CloudBay/", AccountId = "guest-fixture-" + enrolled.FixtureId.ToString("N"),
            BucketId = "offline-fixture", KeyId = "offline-fixture", Backups = backups
        });
        boundary = boundary with { DirectoryIdentity = ClientFolderMigration.ReadIdentity(source) };
        Save(root, boundary);
        using (var operation = ClientFolderMigration.Create(plan, FolderItem, State(root), Records(root), new InterruptedRealRuntime()))
        using (var session = MigrationSession.Create(plan, ["a-completed", FolderItem], SessionPath(root),
                   [new CompletedFixture(root, plan.Fingerprint), operation]))
        {
            session.ProgressChanged += progress => AppendProgress(root, progress);
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(item => item.Id == "a-completed").State);
            var failed = session.Items.Single(item => item.Id == FolderItem);
            Assert.AreEqual(MigrationItemState.Failed, failed.State);
            Assert.IsFalse(string.IsNullOrWhiteSpace(failed.Error), "The interruption must be visible in the real migration journal.");
            Assert.IsFalse(session.CanRevert("a-completed"), "Completed selections cannot be reverted with an incomplete folder item.");
        }
        boundary = boundary with { Phase = "Prepared" };
        VerifyPartial(root, boundary);
        Save(root, boundary);
    }

    private static void VerifyPartial(string root, Boundary boundary, bool allowRecreatedFolder = false)
    {
        Assert.IsTrue(boundary.Phase is "Prepared" or "VerifiedBeforeRestart");
        if (!allowRecreatedFolder) Assert.IsFalse(Directory.Exists(boundary.Source), "The old location unexpectedly reappeared before restart.");
        else
        {
            using var operation = ClientFolderMigration.Resume(Plan(root, boundary.Source, boundary.Destination), FolderItem, State(root), Records(root));
            // Production's bounded, metadata-only conflict check is read-only.
            // Unexpected user files still fail before any recovery writes.
            operation.CheckAsync(default).GetAwaiter().GetResult();
        }
        Assert.IsTrue(Directory.Exists(boundary.Destination));
        Assert.AreEqual(boundary.DirectoryIdentity, ClientFolderMigration.ReadIdentity(boundary.Destination));
        Same(boundary.Source, new ClientStorage(State(root)).LoadSettings().RootPath);
        for (var index = 0; index < Selected.Length; index++)
        {
            var name = Selected[index];
            Same(Path.Combine(index == 0 ? boundary.Destination : boundary.Source, name), KnownFolderBackup.GetPath(name));
            CheckPayload(boundary, name, Path.Combine(boundary.Destination, name));
            CheckPayload(boundary, name, boundary.OriginalPaths[name]);
        }
        VerifyUnselected(boundary);
        Assert.AreEqual(CompletedFixture.Content, File.ReadAllText(Path.Combine(root, "completed-selection.txt")));
    }

    private static async Task RecoverAsync(string root, Boundary boundary, bool revert)
    {
        var plan = Plan(root, boundary.Source, boundary.Destination);
        using var operation = ClientFolderMigration.Resume(plan, FolderItem, State(root), Records(root));
        using var session = MigrationSession.Open(SessionPath(root), [new CompletedFixture(root, plan.Fingerprint), operation]);
        session.ProgressChanged += progress => AppendProgress(root, progress);
        Assert.IsFalse(session.CanRevert("a-completed"));
        if (revert) await session.RevertAsync(FolderItem);
        else await session.RetryAsync(FolderItem);
        var recovered = session.Items.Single(item => item.Id == FolderItem);
        Assert.AreEqual(revert ? MigrationItemState.Reverted : MigrationItemState.Completed, recovered.State,
            "Migration recovery failed: " + recovered.Error + ". Saved details: " + SessionPath(root));
        Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(item => item.Id == "a-completed").State);
        Assert.AreEqual(!revert, session.IsComplete);
        if (!revert) Assert.IsFalse(session.CanRevert(FolderItem));
        var expectedRoot = revert ? boundary.Source : boundary.Destination;
        var settings = new ClientStorage(State(root)).LoadSettings();
        Same(expectedRoot, settings.RootPath);
        Assert.AreEqual(boundary.DirectoryIdentity, ClientFolderMigration.ReadIdentity(expectedRoot));
        foreach (var name in Selected)
        {
            Same(Path.Combine(expectedRoot, name), KnownFolderBackup.GetPath(name));
            Same(Path.Combine(expectedRoot, name), settings.Backups.Single(folder => folder.Name == name).DestinationPath);
            CheckPayload(boundary, name, Path.Combine(expectedRoot, name));
            CheckPayload(boundary, name, boundary.OriginalPaths[name]);
        }
        VerifyUnselected(boundary);
        Assert.AreEqual(CompletedFixture.Content, File.ReadAllText(Path.Combine(root, "completed-selection.txt")));
    }

    private static async Task RestoreAsync(string root, Boundary boundary)
    {
        Assert.IsTrue(boundary.Phase is "CompletedAfterRealRestart" or "Reverted" or "RestoringMappings",
            "Do not restore mappings over an incomplete or failed migration. Retain its receipts for recovery.");
        var settings = new ClientStorage(State(root)).LoadSettings();
        Save(root, boundary with { Phase = "RestoringMappings" });
        foreach (var name in Selected)
        {
            var folder = settings.Backups.Single(value => value.Name == name);
            Same(folder.OriginalPath, boundary.OriginalPaths[name]);
            CheckPayload(boundary, name, folder.DestinationPath);
            CheckPayload(boundary, name, folder.OriginalPath);
            // Retry can observe an already restored mapping; never overwrite an external third location.
            if (!Path.GetFullPath(KnownFolderBackup.GetPath(name)).Equals(Path.GetFullPath(folder.OriginalPath), StringComparison.OrdinalIgnoreCase))
            {
                Same(folder.DestinationPath, KnownFolderBackup.GetPath(name));
                await KnownFolderBackup.DisableAsync(folder, default); // Copy only: retains both fixture copies.
            }
            Same(folder.OriginalPath, KnownFolderBackup.GetPath(name));
            CheckPayload(boundary, name, folder.OriginalPath);
        }
        VerifyUnselected(boundary);
        Save(root, boundary with { Phase = "MappingsRestoredEvidenceRetained" });
    }

    private static void ValidateBoundary(string root, Boundary value, bool priorRuntimeRecovery = false)
    {
        Assert.AreEqual(1, value.Schema);
        Same(Path.Combine(root, BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay"), value.Source);
        Same(Path.Combine(root, BuildInfo.Flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet"), value.Destination);
        Assert.AreEqual("CloudInlet-acceptance-" + value.FixtureId.ToString("N") + ".txt", value.FixtureFile);
        Assert.AreEqual(Plan(root, value.Source, value.Destination).Fingerprint, value.PlanFingerprint);
        Assert.IsNotNull(value.DirectoryIdentity);
        if (priorRuntimeRecovery)
        {
            var path = Environment.GetEnvironmentVariable("CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY");
            var expected = Environment.GetEnvironmentVariable("CLOUDINLET_GUEST_PRIOR_RUNTIME_INVENTORY_SHA256");
            Assert.IsFalse(string.IsNullOrWhiteSpace(path));
            var inventory = ReadBounded(path!, 256 * 1024);
            GuestRuntimeTransition.Validate(value.RuntimeSha256, RuntimeHashes(), inventory, expected!);
        }
        else
        {
            var currentRuntime = RuntimeHashes();
            Assert.AreEqual(currentRuntime.Count, value.RuntimeSha256.Count);
            foreach (var binary in currentRuntime)
                Assert.AreEqual(binary.Value, value.RuntimeSha256[binary.Key], "The test or production assembly changed between acceptance stages.");
        }
        foreach (var name in Selected.Append("Downloads"))
        {
            Same(value.OriginalPaths[name], KnownFolderBackup.GetDefaultPath(name));
            Assert.AreEqual(Hash(Payload(value.FixtureId, name)), value.PayloadSha256[name]);
            RequireOrdinaryPath(value.OriginalPaths[name]);
        }
        RequireOrdinaryPath(value.Source); RequireOrdinaryPath(value.Destination);
    }
    private static void VerifyUnselected(Boundary value)
    { Same(value.OriginalPaths["Downloads"], KnownFolderBackup.GetPath("Downloads")); CheckPayload(value, "Downloads", value.OriginalPaths["Downloads"]); }
    private static void CheckPayload(Boundary value, string name, string directory)
    { Assert.AreEqual(value.PayloadSha256[name], Hash(ReadBounded(Path.Combine(directory, value.FixtureFile), 8192)), name + " fixture bytes changed."); }
    private static byte[] Payload(Guid id, string name) => Encoding.UTF8.GetBytes("Generated disposable CloudInlet guest fixture " + id.ToString("D") + " / " + name + "\r\n" + new string('x', 1024));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Dictionary<string, string> RuntimeHashes() => new[]
    {
        typeof(GuestKnownFolderAcceptanceTests).Assembly.Location,
        typeof(MigrationSession).Assembly.Location
    }.ToDictionary(path => Path.GetFileName(path) ?? throw new InvalidDataException("Missing acceptance assembly name."), path =>
    {
        RequireOrdinaryPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }, StringComparer.Ordinal);
    private static string State(string root) => Path.Combine(root, "state");
    private static string Records(string root) => Path.Combine(root, "receipts");
    private static string SessionPath(string root) => Path.Combine(root, "session.json");
    private static MigrationPlan Plan(string root, string source, string destination) => MigrationPlan.Create([
        new("a-completed", MigrationArea.ApplicationState, "Completed fixture", Path.Combine(root, "before.txt"), Path.Combine(root, "completed-selection.txt"), "Preserve this completed selection"),
        new(FolderItem, MigrationArea.SyncFolder, "Disposable Windows folders", source, destination, "Migrate the three explicitly selected fixture folders", Dependencies: ["a-completed"])]);
    private static void Same(string expected, string actual) => Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)), true);
    private static byte[] ReadBounded(string path, int limit)
    {
        RequireOrdinaryPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("Guest fixture metadata exceeds its bound.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static void RequireOrdinaryPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked guest fixture paths are not permitted.");
    }
    private static Boundary Load(string root)
    {
        var plain = ProtectedData.Unprotect(ReadBounded(Path.Combine(root, "boundary.dpapi"), 64 * 1024), BoundaryEntropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Boundary>(plain, Json) ?? throw new InvalidDataException("Missing protected guest receipt."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void Save(string root, Boundary boundary)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(boundary, Json);
        var encrypted = ProtectedData.Protect(bytes, BoundaryEntropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(bytes);
        var destination = Path.Combine(root, "boundary.dpapi");
        RequireOrdinaryPath(destination);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(encrypted); output.Flush(true); }
        File.Move(staging, destination, true);
    }
    private static void AppendProgress(string root, MigrationProgress progress)
    {
        var path = Path.Combine(root, "progress.jsonl"); RequireOrdinaryPath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { timeUtc = DateTimeOffset.UtcNow, progress });
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.WriteByte((byte)'\n'); output.Flush(true);
    }
    private static BootEvidence ReadBootEvidence()
    {
        var command = "$ErrorActionPreference='Stop'; $c=Get-CimInstance Win32_ComputerSystem; $p=Get-CimInstance Win32_ComputerSystemProduct; " +
            "$o=Get-CimInstance Win32_OperatingSystem; $e=Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-General'; Id=12} -MaxEvents 1; " +
            "[ordered]@{FirmwareUuid=$p.UUID; Manufacturer=$c.Manufacturer; Model=$c.Model; LastBootUtc=$o.LastBootUpTime.ToUniversalTime().ToString('O'); " +
            "KernelBootEventUtc=$e.TimeCreated.ToUniversalTime().ToString('O'); KernelBootRecordId=$e.RecordId}|ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(command);
        using var process = Process.Start(start) ?? throw new IOException("Guest identity reader did not start.");
        var readingOutput = process.StandardOutput.ReadToEndAsync();
        var readingErrors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true); // Only this test's read-only metadata helper.
            throw new IOException("The independent guest identity reader timed out before acceptance could proceed.");
        }
        var output = readingOutput.GetAwaiter().GetResult(); var errors = readingErrors.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || output.Length > 16 * 1024)
            throw new IOException("Independent guest firmware and boot evidence could not be read. " + errors);
        return JsonSerializer.Deserialize<BootEvidence>(output, Json) ?? throw new IOException("Guest boot evidence was empty.");
    }

    private sealed class InterruptedRealRuntime : IClientFolderMigrationRuntime
    {
        private readonly ClientFolderMigration.WindowsRuntime _inner = new();
        private bool _interrupted;
        public bool IsRegistered(string root, string account, bool canonical) => _inner.IsRegistered(root, account, canonical);
        public Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials, IProgress<string> progress, CancellationToken token) =>
            _inner.PrepareAndUnregisterAsync(root, account, canonical, credentials, progress, token);
        public Task RegisterAsync(string root, string account, bool canonical, string manifestPath, CancellationToken token) => _inner.RegisterAsync(root, account, canonical, manifestPath, token);
        public string GetKnownFolder(string name) => _inner.GetKnownFolder(name);
        public void ExchangeKnownFolder(string name, string expected, string replacement)
        {
            _inner.ExchangeKnownFolder(name, expected, replacement);
            if (!_interrupted) { _interrupted = true; throw new IOException("Injected guest acceptance interruption after the first actual Windows Shell mapping write."); }
        }
    }
    private sealed class CompletedFixture(string root, string fingerprint) : IMigrationOperation
    {
        internal const string Content = "Completed independent guest selection; preserve across folder Retry or Revert.";
        public string ItemId => "a-completed";
        public string ReviewedPlanFingerprint => fingerprint;
        public bool CanRevert => false;
        public Task CheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(Path.Combine(root, "completed-selection.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(Encoding.UTF8.GetBytes(Content)); file.Flush(true); return Task.CompletedTask;
        }
        public Task VerifyAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Assert.AreEqual(Content, File.ReadAllText(Path.Combine(root, "completed-selection.txt"))); return Task.CompletedTask; }
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken token) => throw new InvalidOperationException("A completed fixture selection cannot be reverted.");
        public Task VerifyRevertedAsync(CancellationToken token) => throw new InvalidOperationException("A completed fixture selection cannot be reverted.");
    }
}
