using System.Security.Cryptography;
using CloudBay.Core;
using Windows.Storage.Provider;

namespace CloudBay.Windows.CloudFiles;

/// <summary>A credential-free integration check using a newly generated, isolated temporary sync root.</summary>
public static class NativePlaceholderSmokeTest
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<string>();
        // The Shell intentionally filters out roots under hidden AppData/Temp. Create a uniquely
        // named temporary folder beside the user's real sync root, and remove it in finally.
        var temporaryBase = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var root = Path.Combine(temporaryBase, "CloudBay-NativeSmoke-" + Guid.NewGuid().ToString("N"));
        var identity = "native-smoke-" + Guid.NewGuid().ToString("N");
        var content = RandomNumberGenerator.GetBytes(800_137);
        var replacement = RandomNumberGenerator.GetBytes(32_779);
        var editedContent = replacement.Concat(System.Text.Encoding.UTF8.GetBytes("local edit")).ToArray();
        var file = new CloudObject("smoke-v1", "sample.bin", content.Length,
            Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant(), DateTimeOffset.UtcNow);
        var downloads = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WindowsPlaceholderService();
        try
        {
            await service.ConnectAsync(root, identity, async (item, offset, length, destination, token) =>
            {
                Interlocked.Increment(ref downloads);
                if (item.FileId == "smoke-failure") throw new HttpRequestException("Simulated offline download.");
                if (item.FileId == "smoke-slow")
                {
                    slowEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                var data = item.FileId == "uploaded-edit" ? editedContent : item.FileId == "smoke-v2" ? replacement : content;
                // Deliberately unaligned network chunks exercise the aligned CfExecute buffer.
                var end = checked((int)(offset + length));
                for (var position = (int)offset; position < end;)
                {
                    var chunk = Math.Min(17_321, end - position);
                    await destination.WriteAsync(data.AsMemory(position, chunk), token);
                    position += chunk;
                }
            }, cancellationToken);
            checks.Add("Registered isolated NTFS sync root with Explorer and connected native callbacks.");
            var path = Path.Combine(root, "nested", "sample.bin");
            await service.CreateOrUpdateAsync(path, file, true, cancellationToken);
            Require(service.IsPlaceholder(path) && !service.IsHydrated(path), "The new file must be an online-only placeholder.");
            checks.Add("Created online-only placeholder with remote identity, size, and timestamp.");

            var read = await Task.Run(() => File.ReadAllBytes(path), cancellationToken);
            Require(content.AsSpan().SequenceEqual(read), "Native hydration must reproduce the cloud bytes.");
            Require(downloads > 0 && service.IsHydrated(path), "Opening the file must invoke the Cloud Files fetch callback.");
            checks.Add("Normal file read hydrated 800137 bytes through native FETCH_DATA/CfExecute with aligned blocks and an EOF tail.");

            await service.SetPinAsync(path, PinMode.AlwaysAvailable, cancellationToken);
            Require(service.IsHydrated(path), "Pinned files must be fully available.");
            await service.FreeSpaceAsync(path, cancellationToken);
            Require(!service.IsHydrated(path), "Free space must dehydrate clean cloud content.");
            read = await Task.Run(() => File.ReadAllBytes(path), cancellationToken);
            Require(content.AsSpan().SequenceEqual(read), "Reading after eviction must rehydrate correctly.");
            checks.Add("Always available, safe free space, and rehydration passed.");

            await service.SetPinAsync(Path.GetDirectoryName(path)!, PinMode.AlwaysAvailable, cancellationToken);

            var version2 = file with { FileId = "smoke-v2", Size = replacement.Length,
                Sha1 = Convert.ToHexString(SHA1.HashData(replacement)), ModifiedUtc = DateTimeOffset.UtcNow };
            await service.CreateOrUpdateAsync(path, version2, true, cancellationToken);
            Require(service.IsHydrated(path), "A changed pinned remote file must hydrate its new content.");
            read = await Task.Run(() => File.ReadAllBytes(path), cancellationToken);
            Require(replacement.AsSpan().SequenceEqual(read), "A changed remote identity must hydrate the new version.");
            checks.Add("Recursive folder pinning and updating a pinned cloud version hydrated fresh bytes.");

            File.AppendAllText(path, "local edit");
            var refused = false;
            try { await service.FreeSpaceAsync(path, cancellationToken); }
            catch (IOException) { refused = true; }
            Require(refused && new FileInfo(path).Length > replacement.Length, "Unuploaded local changes must never be evicted.");
            refused = false;
            try { await service.CreateOrUpdateAsync(path, file, true, cancellationToken); }
            catch (IOException) { refused = true; }
            Require(refused, "A remote placeholder update must preserve dirty local bytes.");
            checks.Add("Refused eviction and remote overwrite of unuploaded local edits.");

            var edited = new CloudObject("uploaded-edit", "sample.bin", new FileInfo(path).Length, null,
                new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
            await service.MarkInSyncAsync(path, edited, cancellationToken);
            Require(service.IsHydrated(path), "Completing a newer placeholder upload must preserve local content.");
            checks.Add("Marked a locally edited existing placeholder in sync after its upload without truncating bytes.");
            await service.FreeSpaceAsync(path, cancellationToken);
            read = await Task.Run(() => File.ReadAllBytes(path), cancellationToken);
            Require(editedContent.AsSpan().SequenceEqual(read), "A backed-up local edit must be safe to evict and rehydrate from its new identity.");
            checks.Add("Uploaded local edits can be safely evicted and hydrated from their updated cloud identity.");

            var localPath = Path.Combine(root, "local.txt");
            await File.WriteAllTextAsync(localPath, "CloudBay upload conversion", cancellationToken);
            var local = new CloudObject("uploaded-local", "local.txt", new FileInfo(localPath).Length, null,
                new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)));
            await service.MarkInSyncAsync(localPath, local, cancellationToken);
            Require(service.IsPlaceholder(localPath) && service.IsHydrated(localPath), "Uploaded local files must convert without discarding local content.");
            checks.Add("Converted an uploaded regular file into an in-sync hydrated cloud placeholder.");

            var removePath = Path.Combine(root, "remove-online.bin");
            var removeFile = file with { FileId = "smoke-remove", Key = "remove-online.bin" };
            await service.CreateOrUpdateAsync(removePath, removeFile, true, cancellationToken);
            var beforeRemovalDownloads = Volatile.Read(ref downloads);
            Require(!await service.TryRemoveVerifiedOnlinePlaceholderAsync(removePath, removeFile with { FileId = "wrong-version" }, cancellationToken),
                "A mismatched cloud identity must not remove an online-only file.");
            Require(File.Exists(removePath), "A refused removal must preserve the placeholder.");
            Require(await service.TryRemoveVerifiedOnlinePlaceholderAsync(removePath, removeFile, cancellationToken),
                "An exact unchanged online-only placeholder must be removable without fetching its contents.");
            Require(!File.Exists(removePath) && Volatile.Read(ref downloads) == beforeRemovalDownloads,
                "Removing an online-only placeholder must not cause a cloud download.");
            Require(!await service.TryRemoveVerifiedOnlinePlaceholderAsync(localPath, local, cancellationToken) && File.Exists(localPath),
                "Online-only cleanup must retain resident local files for independent content verification.");
            checks.Add("Exact online-only cleanup refused a changed identity and resident data, and removed cloud metadata without downloading payloads.");

            var failurePath = Path.Combine(root, "offline.bin");
            await service.CreateOrUpdateAsync(failurePath, file with { FileId = "smoke-failure" }, true, cancellationToken);
            refused = false;
            try { await Task.Run(() => File.ReadAllBytes(failurePath), cancellationToken); }
            catch (IOException) { refused = true; }
            Require(refused, "Failed cloud downloads must complete waiting file reads with a Windows error.");
            checks.Add("Simulated offline hydration completed pending Windows I/O with cloud network failure.");

            var slowPath = Path.Combine(root, "pending.bin");
            await service.CreateOrUpdateAsync(slowPath, file with { FileId = "smoke-slow" }, true, cancellationToken);
            var pendingRead = Task.Run(() => File.ReadAllBytes(slowPath), cancellationToken);
            await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await service.DisconnectAsync();
            refused = false;
            try { await pendingRead.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (IOException) { refused = true; }
            Require(refused, "Disconnecting must cancel active hydration and release waiting file readers.");
            checks.Add("Disconnect cancelled an active network hydration and released the blocked native reader.");
            Require(File.Exists(localPath), "Disconnect must retain user data.");
            Require(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(x => x.Id == service.RegistrationId),
                "Disconnect must retain the sync root registration.");
            checks.Add("Disconnected callbacks while retaining sync root registration and local files.");
            return checks;
        }
        finally
        {
            await service.DisconnectAsync();
            // Only this generated test registration is removed. Production Dispose never unregisters.
            if (service.RegistrationId is { } registrationId) StorageProviderSyncRootManager.Unregister(registrationId);
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(temporaryBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CloudBay-NativeSmoke-", StringComparison.Ordinal))
                throw new IOException("Refusing to clean a directory outside the isolated smoke-test root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
