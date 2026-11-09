using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

[TestClass]
public sealed class CloudPrefixProcessRecoveryAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private sealed record Boundary(Guid Run, int ProcessId, long ProcessStartUtcTicks, string Fingerprint,
        string AcknowledgmentSha256, CloudPrefixVersion[] OriginalVersions, string[] DestinationIds);
    private static byte[] Protect(byte[] value) => CloudPrefixProviderAcceptanceTests.Protect(value);
    private static byte[] Unprotect(byte[] value) => CloudPrefixProviderAcceptanceTests.Unprotect(value);

    [TestMethod]
    [TestCategory("LiveProvider")]
    public async Task SeparateProcessResumesOnlyAuthenticatedAcknowledgedVersions()
    {
        var phase = Environment.GetEnvironmentVariable("CLOUDINLET_B2_PROCESS_ACCEPTANCE_PHASE");
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_MIGRATION_ACCEPTANCE") != "1" || phase is not ("prepare" or "resume"))
            Assert.Inconclusive("Explicit isolated B2 process acceptance phase is required. No requests were sent.");
        if (!Guid.TryParseExact(Environment.GetEnvironmentVariable("CLOUDINLET_B2_PROCESS_ACCEPTANCE_RUN"), "N", out var run) || run == Guid.Empty)
            Assert.Inconclusive("A fresh explicit process-acceptance run ID is required.");
        var root = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        var bucketName = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(bucketName))
            Assert.Inconclusive("An absolute durable evidence directory and expected test bucket are required.");
        if (Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
            Assert.Inconclusive("Process acceptance requires explicit mutation mode after separate read-only preflight.");
        var directory = Path.Combine(root!, "cloud-prefix-process-acceptance-" + run.ToString("N"));
        if (phase == "prepare")
        {
            Assert.IsFalse(Directory.Exists(directory), "Preparation never reuses a fixture.");
            Directory.CreateDirectory(directory);
        }
        else Assert.IsTrue(Directory.Exists(directory), "Recovery requires existing evidence.");
        var requestDirectory = Path.Combine(directory, "requests-" + phase);
        Assert.IsFalse(Directory.Exists(requestDirectory), "An attempted phase cannot be blindly repeated.");
        Directory.CreateDirectory(requestDirectory);
        using var process = Process.GetCurrentProcess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var transport = new CloudPrefixAcceptanceTransport(requestDirectory);
        using var store = new B2CloudStore(transport);
        var guards = new List<SafeFileHandle>();
        var stage = "preflight";
        var succeeded = false;
        var differentProcess = false;
        string? failureType = null, failureStack = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        try
        {
            ClientRegistrationMigration.PinAncestors(directory, guards);
            var credentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
            var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(credentialPath), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            B2Credentials credentials;
            try { credentials = JsonSerializer.Deserialize<B2Credentials>(plain)!; }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var account = await store.ConnectAsync(credentials, deadline.Token);
            credentials = null!;
            var buckets = await store.ListBucketsAsync(deadline.Token);
            Assert.AreEqual(1, buckets.Count);
            Assert.AreEqual(bucketName, buckets[0].Name);
            Assert.AreEqual(buckets[0].Id, account.AllowedBucketId);
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run);
            transport.Scope = scope;
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Isolated process acceptance", scope.Source, scope.Destination, "Retain original fixture history")]);
            var copyPath = Path.Combine(directory, "copies.dpapi");
            var migrationPath = Path.Combine(directory, "migration.dpapi");
            var boundaryPath = Path.Combine(directory, "boundary.dpapi");
            var inventory = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token);
            if (phase == "prepare")
            {
                scope.EnableWrites(account, inventory);
                stage = "seed-isolated-history";
                foreach (var name in new[] { "hidden.txt", "visible.txt" })
                {
                    scope.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token));
                    var bytes = Payload(name, run);
                    using var input = new MemoryStream(bytes, false);
                    await store.UploadAsync(scope.Bucket, scope.Source + name, input, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
                        new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), cancellationToken: deadline.Token);
                    if (name == "hidden.txt")
                    {
                        scope.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token));
                        using var hidden = await transport.PostAsync("b2_hide_file", new { bucketId = scope.Bucket, fileName = scope.Source + name }, deadline.Token);
                    }
                }
                var before = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token);
                Assert.AreEqual(3, before.SourceVersions.Count);
                using var copies = new ProtectedB2CloudPrefixCopyJournal(copyPath, Protect, Unprotect);
                using var journal = new ProtectedCloudPrefixMigrationJournal(migrationPath, Protect, Unprotect);
                using var pause = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, new Binding(directory, plan.Fingerprint), journal);
                stage = "pause-after-durable-acknowledgments";
                var observedPause = false;
                try
                {
                    await operation.ApplyAsync(new ImmediateProgress(value =>
                    {
                        if (value.Operation == "Verified cloud version" && value.Completed == 2) pause.Cancel();
                        Assert.AreNotEqual("Cloud folder migrated", value.Operation);
                    }), pause.Token);
                }
                catch (OperationCanceledException) when (pause.IsCancellationRequested && !deadline.IsCancellationRequested) { observedPause = true; }
                Assert.IsTrue(observedPause);
                var checkpoint = journal.Read()!;
                Assert.AreEqual(CloudPrefixMigrationPhase.Copying, checkpoint.Phase);
                Assert.IsTrue(checkpoint.Entries.All(value => !value.RequestPending));
                var destinationIds = checkpoint.Entries.Where(value => value.Destination is not null).Select(value => value.Destination!.FileId).ToArray();
                Assert.AreEqual(2, destinationIds.Length);
                var after = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token);
                Assert.AreEqual(JsonSerializer.Serialize(before.SourceVersions), JsonSerializer.Serialize(after.SourceVersions));
                CollectionAssert.AreEquivalent(destinationIds, after.DestinationVersions.Select(value => value.FileId).ToArray());
                Assert.AreEqual(5, transport.Mutations);
                var ledger = await File.ReadAllBytesAsync(Path.Combine(requestDirectory, "owned-versions.jsonl"));
                WriteProtected(boundaryPath, new Boundary(run, process.Id, process.StartTime.ToUniversalTime().Ticks, plan.Fingerprint,
                    Convert.ToHexString(SHA256.HashData(ledger)), before.SourceVersions.ToArray(), destinationIds));
                stage = "prepared-for-separate-process";
            }
            else
            {
                stage = "authenticate-process-boundary";
                Assert.IsTrue(File.Exists(copyPath) && File.Exists(migrationPath) && File.Exists(boundaryPath));
                var boundary = ReadProtected<Boundary>(boundaryPath);
                Assert.AreEqual(run, boundary.Run); Assert.AreEqual(plan.Fingerprint, boundary.Fingerprint);
                Assert.IsFalse(boundary.ProcessId == process.Id && boundary.ProcessStartUtcTicks == process.StartTime.ToUniversalTime().Ticks);
                try
                {
                    using var old = Process.GetProcessById(boundary.ProcessId);
                    Assert.AreNotEqual(boundary.ProcessStartUtcTicks, old.StartTime.ToUniversalTime().Ticks, "The preparing process must have exited.");
                }
                catch (ArgumentException) { }
                differentProcess = true;
                var ledger = await File.ReadAllBytesAsync(Path.Combine(directory, "requests-prepare", "owned-versions.jsonl"));
                Assert.AreEqual(boundary.AcknowledgmentSha256, Convert.ToHexString(SHA256.HashData(ledger)));
                var acknowledged = Encoding.UTF8.GetString(ledger).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => JsonSerializer.Deserialize<CloudPrefixAcceptanceScope.OwnedVersion>(value)!).ToArray();
                using var copies = new ProtectedB2CloudPrefixCopyJournal(copyPath, Protect, Unprotect);
                using var journal = new ProtectedCloudPrefixMigrationJournal(migrationPath, Protect, Unprotect);
                var checkpoint = journal.Read()!;
                Assert.AreEqual(plan.Fingerprint, checkpoint.PlanFingerprint);
                Assert.AreEqual(CloudPrefixMigrationPhase.Copying, checkpoint.Phase);
                Assert.IsTrue(checkpoint.Entries.All(value => !value.RequestPending));
                CollectionAssert.AreEquivalent(boundary.DestinationIds, checkpoint.Entries.Where(value => value.Destination is not null).Select(value => value.Destination!.FileId).ToArray());
                var authenticatedVersions = checkpoint.Entries.Select(value => value.Source).Concat(checkpoint.Entries.Where(value => value.Destination is not null).Select(value => value.Destination!)).ToArray();
                Assert.AreEqual(authenticatedVersions.Length, acknowledged.Length);
                foreach (var receipt in acknowledged)
                    Assert.AreEqual(1, authenticatedVersions.Count(value => value.FileId == receipt.Id && value.Key == receipt.Key));
                Assert.AreEqual(JsonSerializer.Serialize(boundary.OriginalVersions), JsonSerializer.Serialize(inventory.SourceVersions));
                scope.RestoreForRecovery(account, inventory, acknowledged);
                var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies);
                var binding = new Binding(directory, plan.Fingerprint);
                await binding.VerifyOriginalAsync(deadline.Token);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
                stage = "resume-in-new-process";
                var completions = 0;
                await operation.ApplyAsync(new ImmediateProgress(value => { if (value.Operation == "Cloud folder migrated") completions++; }), deadline.Token);
                await operation.VerifyAsync(deadline.Token);
                Assert.AreEqual(1, completions); Assert.IsFalse(operation.CanRevert);
                var after = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token);
                Assert.AreEqual(JsonSerializer.Serialize(boundary.OriginalVersions), JsonSerializer.Serialize(after.SourceVersions));
                Assert.AreEqual(3, after.DestinationVersions.Count);
                foreach (var id in boundary.DestinationIds) Assert.AreEqual(1, after.DestinationVersions.Count(value => value.FileId == id));
                Assert.AreEqual(1, transport.Mutations, "Only the remaining version may be created after process restart.");
                foreach (var mapping in operation.VersionMappings.Where(value => value.Original.Action == "upload"))
                {
                    var migrated = mapping.Migrated;
                    using var output = new MemoryStream();
                    await store.DownloadAsync(new(migrated.FileId, migrated.Key, migrated.Size, migrated.Sha1, DateTimeOffset.FromUnixTimeMilliseconds(migrated.UploadedAtMillis)),
                        output, cancellationToken: deadline.Token);
                    CollectionAssert.AreEqual(Payload(mapping.Original.Key[scope.Source.Length..], run), output.ToArray());
                }
                scope.EnableCleanup(after);
                stage = "exact-owned-cleanup";
                foreach (var owned in scope.Owned.ToArray())
                {
                    scope.RequireRemainingInventory(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token));
                    using var deleted = await transport.PostAsync("b2_delete_file_version", new { fileName = owned.Key, fileId = owned.Id }, deadline.Token);
                    Assert.AreEqual(owned.Id, deleted.RootElement.GetProperty("fileId").GetString());
                    Assert.AreEqual(owned.Key, deleted.RootElement.GetProperty("fileName").GetString());
                }
                var empty = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, deadline.Token);
                Assert.AreEqual(0, empty.SourceVersions.Count + empty.DestinationVersions.Count + empty.UnfinishedUploads.Count);
                Assert.AreEqual(7, transport.Mutations);
                stage = "complete";
            }
            succeeded = true;
        }
        catch (Exception error)
        {
            failureType = error.GetType().FullName; failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("Isolated process recovery stopped at " + stage + ". Retained exact fixture and metadata for review; no broad cleanup.");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, phase + "-report.json"), JsonSerializer.Serialize(new
            {
                run, phase, stage, succeeded, processId = process.Id, processStartUtc = process.StartTime.ToUniversalTime(), differentProcess,
                transport.Mutations, transport.PayloadDownloads, payloadDiskWrites = 0, failureType, failureStack, providerFailure,
                scope = "separate process after a clean durable pause; not abrupt kill, real Windows reboot or desktop binding"
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var guard in guards) guard.Dispose();
        }
    }

    private static byte[] Payload(string name, Guid run) => Encoding.UTF8.GetBytes("Process recovery fixture " + name + " " + run.ToString("N"));
    private static void WriteProtected<T>(string path, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(Protect(plain)); file.Flush(true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static T ReadProtected<T>(string path)
    {
        var plain = Unprotect(File.ReadAllBytes(path));
        try { return JsonSerializer.Deserialize<T>(plain)!; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private sealed class ImmediateProgress(Action<MigrationProgress> callback) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => callback(value); }
    private sealed class Binding(string directory, string fingerprint) : ICloudPrefixMigrationBinding
    {
        private readonly string _path = Path.Combine(directory, "binding.dpapi");
        public string MaintenanceReceipt => fingerprint;
        public Task VerifyMaintenanceAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task VerifyOriginalAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Assert.IsFalse(File.Exists(_path)); return Task.CompletedTask; }
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        { token.ThrowIfCancellationRequested(); WriteProtected(_path, mappings); return Task.CompletedTask; }
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(JsonSerializer.Serialize(mappings), JsonSerializer.Serialize(ReadProtected<CloudPrefixVersionMapping[]>(_path)));
            return Task.CompletedTask;
        }
    }
}
