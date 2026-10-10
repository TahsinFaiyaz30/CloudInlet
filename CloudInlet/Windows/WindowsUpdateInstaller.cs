using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudInlet.Core.Updates;

namespace CloudInlet.Windows;

/// <summary>The copied worker lives outside the installation so Windows can replace all application binaries.</summary>
public sealed class WindowsUpdateInstaller(UpdateInstallation installation, string cacheDirectory,
    Func<Task<bool>> restartBackground, Action requestExit) : IUpdateInstaller
{
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
        try
        {
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
            var helper = Path.Combine(workerDirectory, id + ".exe");
            var request = Path.Combine(workerDirectory, id + ".json");
            var readiness = Path.Combine(workerDirectory, id + ".ready.json");
            File.Copy(bundledHelper, helper);
            var config = bundledHelper + ".config";
            if (File.Exists(config)) { EnsureUnlinked(config); File.Copy(config, helper + ".config"); }
            using var parent = Process.GetCurrentProcess();
            var reopenInBackground = await restartBackground().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(request, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, parentProcessId = Environment.ProcessId, parentStartUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
                installDirectory = installation.Directory, buildFlavor = package.Identity.BuildFlavor.ToString(),
                installerKind = package.Identity.InstallerKind.ToString(), installedVersion = package.Identity.Version,
                targetVersion = package.Candidate.Version, packagePath = payload,
                packageSha256 = package.Candidate.Asset.Sha256, packageSize = package.Candidate.Asset.Size,
                resultPath = Path.Combine(cache, "install-result.json"), readinessPath = readiness, restartBackground = reopenInBackground
            }));
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
            requestExit();
        }
        catch (Win32Exception error) { throw new IOException("Windows could not start the update installer: " + error.Message, error); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or UnauthorizedAccessException)
        { throw new IOException("The update helper could not complete its handoff: " + error.Message, error); }
    }

    internal static string? ReadFailureAndPruneHosts(string cacheDirectory)
    {
        var cache = Path.GetFullPath(cacheDirectory);
        EnsureUnlinked(cache);
        // Preserve the durable update-cache ownership protocol across CloudInlet versions.
        var owner = Path.Combine(cache, ".cloudbay-update-cache-v1");
        EnsureUnlinked(owner);
        if (!File.Exists(owner) || new FileInfo(owner).Length != "CloudBay update cache v1\n".Length ||
            File.ReadAllText(owner) != "CloudBay update cache v1\n") return null;
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
            completed >= DateTimeOffset.UtcNow.AddDays(-7) && completed <= DateTimeOffset.UtcNow.AddMinutes(5) &&
            root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False &&
            root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        return null;
    }

    internal static void DeleteInactiveHosts(string cacheDirectory)
    {
        var cache = Path.GetFullPath(cacheDirectory);
        EnsureUnlinked(cache);
        var lockPath = Path.Combine(cache, "update-install.lock");
        EnsureUnlinked(lockPath);
        using var installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var owner = Path.Combine(cache, ".cloudbay-update-cache-v1");
        EnsureUnlinked(owner);
        if (!File.Exists(owner) || new FileInfo(owner).Length != "CloudBay update cache v1\n".Length ||
            File.ReadAllText(owner) != "CloudBay update cache v1\n") throw new IOException("The update cache could not be verified.");
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
