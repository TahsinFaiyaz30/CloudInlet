using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CloudBay.Core;
using CloudBay.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Provider;
using static CloudBay.Windows.CloudFiles.CloudFilesNative;

namespace CloudBay.Tests;

/// <summary>Inspects native Cloud Files metadata without requesting any file content.</summary>
[TestClass]
[DoNotParallelize]
public sealed class NativeMetadataScanTests
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task RepeatedOnlineOnlyMetadataInspectionDoesNotHydrateContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = fixture.Path("Pictures/online-only.bin");
        var cloud = fixture.AddCloudFile("online-only", "Pictures/online-only.bin", 131_101);
        await fixture.Service.CreateOrUpdateAsync(path, cloud, true, fixture.Token);

        for (var index = 0; index < 100; index++)
        {
            var state = fixture.Service.GetFileState(path);
            Assert.IsTrue(state.IsPlaceholder);
            Assert.IsFalse(state.IsHydrated, "Metadata inspection must not make online-only content resident.");
            Assert.IsFalse(state.HasLocalChanges, "An untouched verified cloud identity must remain clean.");
        }

        Assert.AreEqual(0, fixture.HydrationRequests,
            "The scan must not ask the provider to hydrate even once while inspecting placeholder state.");
        Assert.IsFalse(fixture.Service.GetFileState(path).IsHydrated);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ResidentSameSizeEditWithRestoredWriteTimeStillReportsNativeDirtyContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = fixture.Path("Documents/resident.bin");
        var cloud = fixture.AddCloudFile("resident", "Documents/resident.bin", 65_057);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, fixture.Content(cloud.FileId), fixture.Token);
        var originalTime = File.GetLastWriteTimeUtc(path);
        await fixture.Service.MarkInSyncAsync(path, cloud with { ModifiedUtc = new(originalTime) }, fixture.Token);
        var initial = fixture.Service.GetFileState(path);
        Assert.IsTrue(initial.IsPlaceholder);
        Assert.IsTrue(initial.IsHydrated);
        Assert.IsFalse(initial.HasLocalChanges);

        var edited = fixture.Content(cloud.FileId).ToArray();
        var changedOffset = edited.Length / 2;
        edited[changedOffset] ^= 0x5A;
        // Update the resident placeholder in place. Create/truncate writes can replace
        // its cloud identity on Windows and belong to the atomic replacement scenario.
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous))
        {
            stream.Position = changedOffset;
            await stream.WriteAsync(edited.AsMemory(changedOffset, 1), fixture.Token);
            await stream.FlushAsync(fixture.Token);
        }
        File.SetLastWriteTimeUtc(path, originalTime);
        Assert.AreEqual(cloud.Size, new FileInfo(path).Length);
        Assert.AreEqual(originalTime, File.GetLastWriteTimeUtc(path));

        var changed = fixture.Service.GetFileState(path);
        Assert.IsTrue(changed.IsPlaceholder);
        Assert.IsTrue(changed.IsHydrated);
        Assert.IsTrue(changed.HasLocalChanges,
            "Native dirty ranges must detect edits even when size and last-write time match the sync baseline.");
        Assert.AreEqual(0, fixture.HydrationRequests, "Inspecting already resident bytes must not start a network request.");
        CollectionAssert.AreEqual(edited, await File.ReadAllBytesAsync(path, fixture.Token));
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task AtomicOrdinaryReplacementAtTrackedPathReportsLocalChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = fixture.Path("Documents/replaced.bin");
        var cloud = fixture.AddCloudFile("replaced", "Documents/replaced.bin", 65_061);
        await fixture.Service.CreateOrUpdateAsync(path, cloud, true, fixture.Token);
        Assert.IsTrue(fixture.Service.GetFileState(path).IsPlaceholder);
        var originalTime = File.GetLastWriteTimeUtc(path);
        var replacement = fixture.Path("Documents/replacement.tmp");
        var bytes = fixture.Content(cloud.FileId).ToArray();
        bytes[0] ^= 0x3F;
        await File.WriteAllBytesAsync(replacement, bytes, fixture.Token);
        File.SetLastWriteTimeUtc(replacement, originalTime);
        File.Move(replacement, path, overwrite: true);

        var state = fixture.Service.GetFileState(path);
        Assert.IsFalse(state.IsPlaceholder);
        Assert.IsTrue(state.IsHydrated);
        Assert.IsTrue(state.HasLocalChanges,
            "An ordinary replacement must be reverified even when its timestamp and size match a previous placeholder.");
        Assert.AreEqual(cloud.Size, new FileInfo(path).Length);
        Assert.AreEqual(originalTime, File.GetLastWriteTimeUtc(path));
        Assert.AreEqual(0, fixture.HydrationRequests);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, fixture.Token));
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task FileSymbolicLinkIsRejectedInsteadOfReadingItsTargetState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = fixture.OutsidePath("target.bin");
        var original = RandomNumberGenerator.GetBytes(4099);
        await File.WriteAllBytesAsync(target, original, fixture.Token);
        var link = fixture.Path("linked.bin");
        File.CreateSymbolicLink(link, target);
        try
        {
            Assert.ThrowsException<IOException>(() => fixture.Service.GetFileState(link));
            Assert.AreEqual(0, fixture.HydrationRequests);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(target, fixture.Token));
        }
        finally { File.Delete(link); }
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task PreviouslyInspectedDirectoryReplacedByJunctionIsRecheckedOnEveryCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        var directory = fixture.Path("Documents");
        Directory.CreateDirectory(directory);
        var path = fixture.Path("Documents/report.bin");
        await File.WriteAllTextAsync(path, "ordinary resident data", fixture.Token);
        Assert.IsTrue(fixture.Service.GetFileState(path).HasLocalChanges);

        var target = fixture.OutsidePath("Target");
        Directory.CreateDirectory(target);
        var targetFile = System.IO.Path.Combine(target, "report.bin");
        const string retained = "unrelated target must remain untouched";
        await File.WriteAllTextAsync(targetFile, retained, fixture.Token);
        File.Delete(path);
        Directory.Delete(directory, recursive: false);
        await CreateJunctionAsync(directory, target, fixture.Token);
        try
        {
            Assert.ThrowsException<IOException>(() => fixture.Service.GetFileState(path),
                "A previous successful inspection must not cache permission to follow a replaced parent directory.");
            Assert.AreEqual(0, fixture.HydrationRequests);
            Assert.AreEqual(retained, await File.ReadAllTextAsync(targetFile, fixture.Token));
        }
        finally { Directory.Delete(directory, recursive: false); }
    }

    private static async Task CreateJunctionAsync(string link, string target, CancellationToken token)
    {
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path " +
            Quote(link) + " -Target " + Quote(target) + " | Out-Null");
        using var process = Process.Start(start) ?? throw new IOException("Could not create the isolated junction fixture.");
        var error = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        await output;
        Assert.AreEqual(0, process.ExitCode, await error);
        Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private readonly string _container;
        private readonly ConcurrentDictionary<string, byte[]> _versions = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(65));
        private int _hydrationRequests;
        public WindowsPlaceholderService Service { get; } = new();
        public string Root { get; }
        public CancellationToken Token => _timeout.Token;
        public int HydrationRequests => Volatile.Read(ref _hydrationRequests);

        private Fixture()
        {
            _container = System.IO.Path.Combine(_parent, "CloudBay-NativeMetadata-" + Guid.NewGuid().ToString("N"));
            Root = System.IO.Path.Combine(_container, "Root");
            Directory.CreateDirectory(OutsidePath(""));
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                await fixture.Service.ConnectAsync(fixture.Root, "native-metadata-" + System.IO.Path.GetFileName(fixture._container),
                    async (cloud, offset, length, destination, token) =>
                    {
                        Interlocked.Increment(ref fixture._hydrationRequests);
                        var content = fixture._versions[cloud.FileId];
                        await destination.WriteAsync(content.AsMemory(checked((int)offset), checked((int)length)), token);
                    }, fixture.Token);
                // Keep inspection independent of this profile's inherited Always keep preference.
                using var handle = CreateFileW(fixture.Root, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                Marshal.ThrowExceptionForHR(CfSetPinState(handle, 0, 0, IntPtr.Zero));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public string OutsidePath(string relative) => System.IO.Path.Combine(_container, "Outside", relative);
        public byte[] Content(string id) => _versions[id];

        public CloudObject AddCloudFile(string id, string key, int size)
        {
            var bytes = RandomNumberGenerator.GetBytes(size);
            _versions[id] = bytes;
            return new(id, key, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)), DateTimeOffset.UtcNow);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.DisconnectAsync();
                if (Service.RegistrationId is { } id) StorageProviderSyncRootManager.Unregister(id);
                var resolved = System.IO.Path.GetFullPath(_container);
                if (!string.Equals(System.IO.Path.GetDirectoryName(resolved), System.IO.Path.GetFullPath(_parent), StringComparison.OrdinalIgnoreCase) ||
                    !System.IO.Path.GetFileName(resolved).StartsWith("CloudBay-NativeMetadata-", StringComparison.Ordinal))
                    throw new IOException("Refusing to remove data outside the generated native metadata fixture.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
            }
            finally { await Service.DisposeAsync(); _timeout.Dispose(); }
        }
    }
}
