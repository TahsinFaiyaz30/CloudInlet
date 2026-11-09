using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudInlet.Core.Updates;

namespace CloudInlet.Windows;

/// <summary>The copied worker lives outside the installation so Windows can replace all application binaries.</summary>
public sealed class WindowsUpdateInstaller(UpdateInstallation installation, string cacheDirectory,
    Func<Task<bool>> restartBackground, Action requestExit,
    Func<VerifiedUpdatePackage, Task<string?>>? reviewInstallationMigration = null) : IUpdateInstaller
{
    public void EnsureRecoveryAllowsMaintenance() => EnsureRecoveryAllowsCleanup(cacheDirectory, installation.Identity.BuildFlavor.ToString());
    internal static bool IsInstallationInProgress(string cacheDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(cacheDirectory), "update-install.lock");
        EnsureUnlinked(path);
        try { using var available = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return true; }
    }
    public async Task InstallAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!installation.CanInstall || package.Identity != installation.Identity)
            throw new InvalidOperationException("The downloaded update does not match this installation.");
        string? handoffPath = null, handoffId = null, handoffHash = null;
        try
        {
            using (var recovery = new CloudInlet.Packaging.InstallerMigrationRecovery(package.Identity.BuildFlavor.ToString()))
                recovery.EnsureNoPending();
            CloudInlet.Packaging.ExeMigrationReceipt.EnsureNoPending(package.Identity.BuildFlavor.ToString());
            var migrationTarget = reviewInstallationMigration is null ? null : await reviewInstallationMigration(package).ConfigureAwait(false);
            if (migrationTarget is not null)
                migrationTarget = CloudInlet.Packaging.InstallationMigrationPolicy.Validate(installation.Directory, migrationTarget,
                    installation.Identity.BuildFlavor.ToString(), installation.Identity.InstallerKind.ToString(),
                    installation.Identity.Version, package.Candidate.Version);
            var cache = Path.GetFullPath(cacheDirectory);
            var payload = Path.GetFullPath(package.Path);
            if (!payload.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update package is outside its download directory.");
            EnsureUnlinked(cache); EnsureUnlinked(payload);
            var bundledHelper = Path.Combine(installation.Directory, "CloudInlet.SetupHelper.exe");
            if (!File.Exists(bundledHelper)) throw new IOException("The update helper is missing. Reinstall the matching installer package.");
            EnsureUnlinked(bundledHelper);
            var workerDirectory = Path.Combine(cache, "host");
            EnsureUnlinked(workerDirectory); Directory.CreateDirectory(workerDirectory); EnsureUnlinked(workerDirectory);
            var id = Guid.NewGuid().ToString("N");
            // A migration must keep live progress visible after this installation's
            // loaded app exits. The verified runtime is copied outside installer ownership.
            var progressExecutable = migrationTarget is null ? null : InstallerProgressHost.Stage(installation.Directory,
                InstallerProgressHost.RuntimeRoot(package.Identity.BuildFlavor.ToString()), id);
            var progressPath = migrationTarget is null ? null : Path.Combine(workerDirectory, id + ".progress.dpapi");
            var helper = Path.Combine(workerDirectory, id + ".exe");
            var request = Path.Combine(workerDirectory, id + ".json");
            var readiness = Path.Combine(workerDirectory, id + ".ready.json");
            File.Copy(bundledHelper, helper);
            var config = bundledHelper + ".config";
            if (File.Exists(config)) { EnsureUnlinked(config); File.Copy(config, helper + ".config"); }
            using var parent = Process.GetCurrentProcess();
            var reopenInBackground = await restartBackground().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = migrationTarget is null ? 1 : 2, parentProcessId = Environment.ProcessId, parentStartUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
                installDirectory = installation.Directory, buildFlavor = package.Identity.BuildFlavor.ToString(),
                installerKind = package.Identity.InstallerKind.ToString(), installedVersion = package.Identity.Version,
                targetVersion = package.Candidate.Version, packagePath = payload,
                packageSha256 = package.Candidate.Asset.Sha256, packageSize = package.Candidate.Asset.Size,
                resultPath = Path.Combine(cache, "install-result.json"), readinessPath = readiness, restartBackground = reopenInBackground,
                migrateInstallation = migrationTarget is not null, migrationInstallDirectory = migrationTarget,
                progressId = migrationTarget is null ? null : id, progressPath,
                recoveryObserverExecutable = migrationTarget is not null && package.Identity.InstallerKind == UpdateInstallerKind.Exe ? progressExecutable : null
            });
            using (var output = new FileStream(request, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(requestBytes); output.Flush(true); }
            if (progressPath is not null)
            {
                var hash = CloudInlet.Packaging.InstallerProgressJournal.RequestHash(requestBytes);
                CloudInlet.Packaging.InstallerProgressHandoff.Create(progressPath, id, hash);
                handoffPath = progressPath; handoffId = id; handoffHash = hash;
            }
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--update"); start.ArgumentList.Add(request);
            using var worker = Process.Start(start) ?? throw new IOException("The update installer could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (!File.Exists(readiness))
                {
                    if (worker.HasExited) throw new IOException("The update helper could not validate this installation. Your current app remains open.");
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                }
                EnsureUnlinked(readiness);
                if (new FileInfo(readiness).Length > 4096) throw new IOException("The update helper acknowledgement is invalid.");
                using var acknowledgement = JsonDocument.Parse(await File.ReadAllTextAsync(readiness, deadline.Token).ConfigureAwait(false));
                if (!acknowledgement.RootElement.GetProperty("ready").GetBoolean() ||
                    acknowledgement.RootElement.GetProperty("targetVersion").GetString() != package.Candidate.Version)
                    throw new IOException("The update helper did not confirm the requested update.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("The update helper did not become ready. Your current app remains open."); }
            if (progressExecutable is not null)
            {
                await InstallerProgressHost.LaunchAsync(progressExecutable, progressPath!, id,
                    handoffHash!, worker, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                CloudInlet.Packaging.InstallerProgressHandoff.Commit(progressPath!, id, handoffHash!);
            }
            requestExit();
            handoffPath = null; // Only this successful handoff authorizes installation after parent exit.
        }
        catch (Win32Exception error) { throw new IOException("Windows could not start the update installer: " + error.Message, error); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { throw new IOException("The update helper could not complete its handoff: " + error.Message, error); }
        finally
        {
            if (handoffPath is not null)
            {
                try { CloudInlet.Packaging.InstallerProgressHandoff.Cancel(handoffPath, handoffId!, handoffHash!); }
                catch (Exception error)
                {
                    throw new IOException("The update handoff could not be canceled. Keep CloudInlet open until the update helper finishes waiting, then retry the update. " + error.Message, error);
                }
            }
        }
    }

    internal static string? ReadFailureAndPruneHosts(string cacheDirectory, string? progressRuntimeRoot = null, string? buildFlavor = null)
    {
        var cache = Path.GetFullPath(cacheDirectory);
        EnsureUnlinked(cache);
        if (buildFlavor is not null)
        {
            CloudInlet.Packaging.ExeMigrationReceipt.EnsureNoPending(buildFlavor);
            var recovery = ResolveInstallationRecovery(cache, buildFlavor);
            if (recovery.Pending) return recovery.Message;
        }
        // Preserve the durable update-cache ownership protocol across CloudInlet versions.
        var owner = Path.Combine(cache, ".cloudbay-update-cache-v1");
        EnsureUnlinked(owner);
        if (!File.Exists(owner) || new FileInfo(owner).Length != "CloudBay update cache v1\n".Length ||
            File.ReadAllText(owner) != "CloudBay update cache v1\n") return null;
        InstallerProgressHost.Prune(cache);
        if (progressRuntimeRoot is not null) InstallerProgressHost.Prune(progressRuntimeRoot);
        var host = Path.Combine(cache, "host");
        EnsureUnlinked(host);
        if (Directory.Exists(host))
            foreach (var path in Directory.EnumerateFiles(host))
            {
                if (!Regex.IsMatch(Path.GetFileName(path), "^[a-f0-9]{32}\\.(exe|exe\\.config|json|ready\\.json)$", RegexOptions.CultureInvariant)) continue;
                EnsureUnlinked(path);
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) continue;
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    File.Delete(path);
                }
                catch (IOException) { /* A still-running worker retains its files. */ }
            }
        var result = Path.Combine(cache, "install-result.json");
        EnsureUnlinked(result);
        if (!File.Exists(result) || new FileInfo(result).Length > 64 * 1024) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(result));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("completedUtc", out var completion) &&
            completion.ValueKind == JsonValueKind.String && completion.TryGetDateTimeOffset(out var completed) &&
            completed <= DateTimeOffset.UtcNow.AddMinutes(5) &&
            ((root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
             (root.TryGetProperty("attentionRequired", out var attention) && attention.ValueKind == JsonValueKind.True) ||
             (root.TryGetProperty("migrationRequiresRestart", out var restart) && restart.ValueKind == JsonValueKind.True)) &&
            root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        return null;
    }

    internal static void DeleteInactiveHosts(string cacheDirectory, string? progressRuntimeRoot = null, string? buildFlavor = null)
    {
        var cache = Path.GetFullPath(cacheDirectory);
        EnsureUnlinked(cache);
        var lockPath = Path.Combine(cache, "update-install.lock");
        EnsureUnlinked(lockPath);
        using var installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureRecoveryAllowsCleanup(cache, buildFlavor);
        var owner = Path.Combine(cache, ".cloudbay-update-cache-v1");
        EnsureUnlinked(owner);
        if (!File.Exists(owner) || new FileInfo(owner).Length != "CloudBay update cache v1\n".Length ||
            File.ReadAllText(owner) != "CloudBay update cache v1\n") throw new IOException("The update cache could not be verified.");
        InstallerProgressHost.Prune(cache, includeRecent: true);
        if (progressRuntimeRoot is not null) InstallerProgressHost.Prune(progressRuntimeRoot, includeRecent: true);
        var host = Path.Combine(cache, "host");
        EnsureUnlinked(host);
        if (Directory.Exists(host))
            foreach (var path in Directory.EnumerateFiles(host))
            {
                if (!Regex.IsMatch(Path.GetFileName(path), "^[a-f0-9]{32}\\.(exe|exe\\.config|json|ready\\.json)$", RegexOptions.CultureInvariant)) continue;
                EnsureUnlinked(path);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                File.Delete(path);
            }
        var result = Path.Combine(cache, "install-result.json");
        EnsureUnlinked(result);
        if (File.Exists(result)) File.Delete(result);
    }

    internal static void EnsureRecoveryAllowsMigrationReview(string cacheDirectory, string buildFlavor,
        Func<CloudInlet.Packaging.InstallerMigrationRecovery>? openRecovery = null)
    {
        // EXE recovery owns its private repair inputs until its existing recovery UI
        // finishes. MSI verification may finish on this boot, before profile review
        // can move those inputs or display an obsolete restart warning.
        CloudInlet.Packaging.ExeMigrationReceipt.EnsureNoPending(buildFlavor);
        var outcome = ResolveInstallationRecovery(cacheDirectory, buildFlavor, openRecovery);
        if (outcome.Pending) throw new IOException(outcome.Message ?? "Installation recovery must finish before reviewing other migrations.");
    }

    internal static void EnsureRecoveryAllowsCleanup(string cacheDirectory, string? buildFlavor = null, string? recoveryRoot = null)
    {
        if (buildFlavor is not null)
        {
            try
            {
                using var recovery = new CloudInlet.Packaging.InstallerMigrationRecovery(buildFlavor, recoveryRoot);
                recovery.EnsureNoPending();
                CloudInlet.Packaging.ExeMigrationReceipt.EnsureNoPending(buildFlavor);
            }
            catch (System.Security.Cryptography.CryptographicException error)
            { throw new IOException("The saved installation recovery record could not be authenticated. Recovery files were retained.", error); }
        }
        var result = Path.Combine(Path.GetFullPath(cacheDirectory), "install-result.json");
        EnsureUnlinked(result);
        if (!File.Exists(result)) return;
        using var input = new FileStream(result, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024) throw new IOException("The saved installation result must be reviewed before deleting update files.");
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("migrationRequiresRestart", out var pending) && pending.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("The restart-required installation migration must be verified before deleting its update and recovery files.");
    }

    internal static CloudInlet.Packaging.InstallerMigrationRecovery.Outcome ResolveInstallationRecovery(
        string cacheDirectory, string buildFlavor, Func<CloudInlet.Packaging.InstallerMigrationRecovery>? openRecovery = null)
    {
        var cache = Path.GetFullPath(cacheDirectory); EnsureUnlinked(cache); Directory.CreateDirectory(cache);
        var lockPath = Path.Combine(cache, "update-install.lock"); EnsureUnlinked(lockPath);
        // An installer may be between saving its intent and starting Windows Installer.
        // Never mistake that running transaction for an untouched failed attempt.
        using var installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var recovery = openRecovery?.Invoke() ?? new CloudInlet.Packaging.InstallerMigrationRecovery(buildFlavor);
        var outcome = recovery.Resolve();
        if (!outcome.Completed && !outcome.Reverted) return outcome;
        var path = Path.Combine(cache, "install-result.json"); EnsureUnlinked(path);
        if (!File.Exists(path)) return outcome;
        byte[] original;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > 64 * 1024) throw new IOException("The saved installation result is too large. Its recovery evidence was retained.");
            original = new byte[(int)input.Length]; input.ReadExactly(original);
        }
        var document = System.Text.Json.Nodes.JsonNode.Parse(original) as System.Text.Json.Nodes.JsonObject;
        if (document is null || document["targetVersion"]?.GetValue<string>() != outcome.TargetVersion ||
            document["installDirectory"]?.GetValue<string>() is not { } directory ||
            !(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(outcome.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(outcome.OriginalDirectory, StringComparison.OrdinalIgnoreCase))) return outcome;
        if (document["migrationRequiresRestart"]?.GetValue<bool>() != true) return outcome;
        document["migrationRequiresRestart"] = false;
        document["migrationCompleted"] = outcome.Completed;
        // Keep unrelated launcher/telemetry errors visible. This JSON is presentation only;
        // the protected receipt above is the sole authority to release the recovery guard.
        if (outcome.Reverted || document["attentionRequired"]?.GetValue<bool>() != true)
            document["message"] = outcome.Message ?? (outcome.Completed ? "Installation migration was verified successfully." : "The original installation was retained. Retry the update when ready.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, document); output.Flush(true); }
        File.Move(temporary, path, true);
        return outcome;
    }

    private static void EnsureUnlinked(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The update path contains a linked file or directory.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
