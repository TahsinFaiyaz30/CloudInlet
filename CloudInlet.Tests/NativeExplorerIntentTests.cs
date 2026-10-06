using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using CloudInlet.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Provider;
using static CloudInlet.Windows.CloudFiles.CloudFilesNative;

namespace CloudInlet.Tests;

/// <summary>Drives Windows pin intent directly, independently of CloudInlet's own pin buttons.</summary>
[TestClass]
[DoNotParallelize]
public sealed class NativeExplorerIntentTests
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task ExplorerPinAndFreeSpaceHydrateEvictAndPreserveDirtyFiles()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var folder = Path.Combine(root, "Documents");
            var path = Path.Combine(folder, "report.bin");
            var bytes = RandomNumberGenerator.GetBytes(180_017);
            versions["original"] = bytes;
            var cloud = new CloudObject("original", "Documents/report.bin", bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)), DateTimeOffset.UtcNow);
            await service.CreateOrUpdateAsync(path, cloud, true, token);

            SetWindowsPin(folder, 1, recurse: true);
            await UntilAsync(() => service.IsHydrated(path), token);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
            Assert.AreEqual(1u, GetPin(folder), "Windows must retain the folder's offline intent.");

            SetWindowsPin(folder, 2, recurse: true);
            await UntilAsync(() => !service.IsHydrated(path), token);
            Assert.AreEqual(2u, GetPin(folder), "Free space must clear the parent folder's pinned intent.");
            var added = Path.Combine(folder, "new.bin");
            await service.CreateOrUpdateAsync(added, cloud with { Key = "Documents/new.bin" }, true, token);
            Assert.AreNotEqual(1u, GetPin(added), "New children must not inherit a stale Always keep preference.");

            SetWindowsPin(path, 1);
            await UntilAsync(() => service.IsHydrated(path), token);
            var edited = bytes.Concat(new byte[] { 2, 4, 6, 8 }).ToArray();
            await File.WriteAllBytesAsync(path, edited, token);
            SetWindowsPin(path, 2);
            await UntilAsync(() => GetPin(path) == 2, token);
            await Task.Delay(300, token);
            CollectionAssert.AreEqual(edited, await File.ReadAllBytesAsync(path, token), "Shell free space must retain every unuploaded local byte.");
            Assert.IsTrue(service.HasLocalChanges(path));

            versions["edited"] = edited;
            await service.MarkInSyncAsync(path, cloud with
            {
                FileId = "edited", Size = edited.Length, Sha1 = Convert.ToHexString(SHA1.HashData(edited)),
                ModifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path))
            }, token);
            await UntilAsync(() => !service.IsHydrated(path), token);
            CollectionAssert.AreEqual(edited, await File.ReadAllBytesAsync(path, token), "Deferred free space must hydrate from the newly verified cloud identity.");
            // Ordinary hydration after unpin must stay cached; its attribute changes are not a
            // second user request to dehydrate the same file immediately.
            await Task.Delay(300, token);
            Assert.IsTrue(service.IsHydrated(path));
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task AutomaticAvailabilityDoesNotDownloadOnlineOnlyContent()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "automatic.bin");
            var content = RandomNumberGenerator.GetBytes(70_019);
            versions["automatic"] = content;
            await service.CreateOrUpdateAsync(path, new CloudObject("automatic", "automatic.bin", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            SetWindowsPin(path, 0);
            Assert.IsFalse(service.IsHydrated(path), "Windows-managed availability must not turn every file into an offline download.");
            SetWindowsPin(path, 1);
            await UntilAsync(() => service.IsHydrated(path), token);
            SetWindowsPin(path, 0);
            Assert.IsTrue(service.IsHydrated(path), "Changing to automatic must preserve clean cached content for Windows Storage Sense.");
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task AlignedHydrationEofWaitsForTransportChecksumAcceptance()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "checksum-failure.bin");
            var content = RandomNumberGenerator.GetBytes(256 * 1024);
            versions["bad-after-write"] = content;
            await service.CreateOrUpdateAsync(path, new CloudObject("bad-after-write", "checksum-failure.bin", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            await Assert.ThrowsExceptionAsync<IOException>(async () => { _ = await Task.Run(() => File.ReadAllBytes(path), token); });
            Assert.IsFalse(service.IsHydrated(path), "A rejected download must not leave an apparently complete native cloud file.");
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task RefreshingFolderAppearanceDoesNotHydrateOnlineOnlyDesktopIni()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var folder = Path.Combine(root, "custom folder");
            var path = Path.Combine(folder, "desktop.ini");
            var content = System.Text.Encoding.Unicode.GetBytes("[.ShellClassInfo]\r\nIconResource=custom.ico,0\r\n");
            versions["online-metadata"] = content;
            await service.CreateOrUpdateAsync(path, new CloudObject("online-metadata", "custom folder/desktop.ini", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            Assert.IsNull(FolderAppearance.GetIconResource(folder));
            Assert.IsFalse(service.IsHydrated(path), "Refreshing a folder card must not download an online-only INI.");
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ReadOnlyRegularFilesAndPlaceholdersCanBeMarkedSyncedWithoutChangingAttributes()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "readonly-object.bin");
            var bytes = RandomNumberGenerator.GetBytes(65_031);
            versions["readonly-object-v1"] = bytes;
            versions["readonly-object-v2"] = bytes;
            await File.WriteAllBytesAsync(path, bytes, token);
            const FileAttributes retained = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
            File.SetAttributes(path, File.GetAttributes(path) | retained);
            try
            {
                var cloud = new CloudObject("readonly-object-v1", "readonly-object.bin", bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
                await service.MarkInSyncAsync(path, cloud, token);
                Assert.IsTrue(service.IsPlaceholder(path), "A readable read-only upload must convert to a native placeholder.");
                Assert.IsFalse(service.HasLocalChanges(path));
                Assert.AreEqual(retained, File.GetAttributes(path) & retained);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));

                await service.MarkInSyncAsync(path, cloud with { FileId = "readonly-object-v2" }, token);
                Assert.IsFalse(service.HasLocalChanges(path), "Existing read-only identity metadata must be marked in sync.");
                Assert.AreEqual(retained, File.GetAttributes(path) & retained);
                await service.FreeSpaceAsync(path, token);
                Assert.IsFalse(service.IsHydrated(path));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token),
                    "Eviction must hydrate from the newly marked read-only identity.");
                Assert.AreEqual(retained, File.GetAttributes(path) & retained);
            }
            finally { ClearGeneratedFixtureReadOnly(path); }
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ReadOnlyPlaceholdersKeepAttributesThroughShellPinningAndRemoteUpdates()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "readonly-cloud.bin");
            var original = RandomNumberGenerator.GetBytes(128_029);
            var replacement = RandomNumberGenerator.GetBytes(256_017);
            versions["readonly-cloud-v1"] = original;
            versions["readonly-cloud-v2"] = replacement;
            var cloud = new CloudObject("readonly-cloud-v1", "readonly-cloud.bin", original.Length,
                Convert.ToHexString(SHA1.HashData(original)), DateTimeOffset.UtcNow);
            await service.CreateOrUpdateAsync(path, cloud, true, token);
            const FileAttributes retained = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
            File.SetAttributes(path, File.GetAttributes(path) | retained);
            try
            {
                SetWindowsPin(path, 1);
                await UntilAsync(() => service.IsHydrated(path), token);
                Assert.AreEqual(retained, File.GetAttributes(path) & retained);
                SetWindowsPin(path, 2);
                await UntilAsync(() => !service.IsHydrated(path), token);
                Assert.AreEqual(retained, File.GetAttributes(path) & retained);
                await service.HydrateAsync(path, token);
                CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path, token));
                await service.SetPinAsync(path, PinMode.AlwaysAvailable, token);
                await service.CreateOrUpdateAsync(path, cloud with { FileId = "readonly-cloud-v2", Size = replacement.Length,
                    Sha1 = Convert.ToHexString(SHA1.HashData(replacement)), ModifiedUtc = DateTimeOffset.UtcNow }, true, token);
                CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(path, token));
                Assert.AreEqual(retained, File.GetAttributes(path) & retained,
                    "A remote version must change its bytes without replacing existing Windows attributes.");
            }
            finally { ClearGeneratedFixtureReadOnly(path); }
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ReadOnlyFilesRemainResidentAndOrdinaryAfterUnregisterPreparation()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "readonly-disconnect.bin");
            var bytes = RandomNumberGenerator.GetBytes(65_019);
            versions["readonly-disconnect"] = bytes;
            await File.WriteAllBytesAsync(path, bytes, token);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            try
            {
                await service.MarkInSyncAsync(path, new("readonly-disconnect", "readonly-disconnect.bin", bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)), new DateTimeOffset(File.GetLastWriteTimeUtc(path))), token);
                await service.FreeSpaceAsync(path, token);
                Assert.IsFalse(service.IsHydrated(path));
                await service.PrepareForUnregisterAsync(token);
                Assert.IsFalse(service.IsPlaceholder(path), "Explicit disconnect preparation must safely revert read-only content.");
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
                Assert.IsTrue((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0,
                    "Disconnect must preserve the original read-only attribute.");
            }
            finally { ClearGeneratedFixtureReadOnly(path); }
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task GenuineNativeMetadataPermissionDenialPreservesReadableLocalBytes()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "metadata-denied.bin");
            var bytes = RandomNumberGenerator.GetBytes(8_017);
            await File.WriteAllBytesAsync(path, bytes, token);
            var cloud = new CloudObject("metadata-denied", "metadata-denied.bin", bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
            var original = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            using var restore = CreateFileW(path, 0x60000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (restore.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var denied = new FileSecurity();
            denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm());
            denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.ChangePermissions, AccessControlType.Deny));
            // OWNER RIGHTS suppresses the owner's implicit WRITE_DAC permission, making this
            // a genuine metadata denial instead of only a read-only content attribute.
            denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
                FileSystemRights.ChangePermissions, AccessControlType.Deny));
            if (!SetKernelObjectSecurity(restore, 4, denied.GetSecurityDescriptorBinaryForm()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var error = await Assert.ThrowsExceptionAsync<COMException>(() => service.MarkInSyncAsync(path, cloud, token));
                Assert.IsTrue(error.HResult < 0, "Real ACL denial must remain explicit rather than be reported as native success.");
                Assert.IsFalse(service.IsPlaceholder(path));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
            }
            finally
            {
                // Restore through the already-authorized handle: never elevate or rewrite
                // user ACLs, and never leave a locked-down generated test fixture behind.
                if (!SetKernelObjectSecurity(restore, 4, original.GetSecurityDescriptorBinaryForm()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task LongNativePathsConvertCreateHydrateAndEvictWithoutChangingTheirVisibleNames()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var folder = Path.Combine(root, new string('a', 110), new string('b', 110));
            Directory.CreateDirectory(folder);
            var localPath = Path.Combine(folder, "local.bin");
            var cloudPath = Path.Combine(folder, "cloud.bin");
            Assert.IsTrue(localPath.Length > 260, "This fixture must exercise a real path beyond MAX_PATH.");
            var localBytes = RandomNumberGenerator.GetBytes(65_023);
            var cloudBytes = RandomNumberGenerator.GetBytes(128_027);
            versions["long-local"] = localBytes;
            versions["long-cloud"] = cloudBytes;
            await File.WriteAllBytesAsync(localPath, localBytes, token);
            await service.MarkInSyncAsync(localPath, new("long-local", Path.GetRelativePath(root, localPath).Replace('\\', '/'),
                localBytes.Length, Convert.ToHexString(SHA1.HashData(localBytes)),
                new DateTimeOffset(File.GetLastWriteTimeUtc(localPath))), token);
            Assert.IsTrue(service.IsPlaceholder(localPath));
            Assert.IsTrue(service.IsHydrated(localPath));
            await service.FreeSpaceAsync(localPath, token);
            Assert.IsFalse(service.IsHydrated(localPath));
            CollectionAssert.AreEqual(localBytes, await File.ReadAllBytesAsync(localPath, token));

            await service.CreateOrUpdateAsync(cloudPath, new("long-cloud", Path.GetRelativePath(root, cloudPath).Replace('\\', '/'),
                cloudBytes.Length, Convert.ToHexString(SHA1.HashData(cloudBytes)), DateTimeOffset.UtcNow), true, token);
            Assert.IsTrue(service.IsPlaceholder(cloudPath));
            Assert.IsFalse(service.IsHydrated(cloudPath));
            SetWindowsPin(cloudPath, 1);
            await UntilAsync(() => service.IsHydrated(cloudPath), token);
            CollectionAssert.AreEqual(cloudBytes, await File.ReadAllBytesAsync(cloudPath, token));
            SetWindowsPin(cloudPath, 2);
            await UntilAsync(() => !service.IsHydrated(cloudPath), token);
            CollectionAssert.AreEqual(cloudBytes, await File.ReadAllBytesAsync(cloudPath, token));
            Assert.IsFalse(localPath.StartsWith(@"\\?\", StringComparison.Ordinal),
                "The extended namespace belongs at the native boundary, not in persisted or visible paths.");
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task IncompatibleHardLinksRemainOrdinaryReadableAndUnchangedAfterNativeMarkFailure()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "linked-original.bin");
            var alias = Path.Combine(root, "linked-alias.bin");
            var bytes = RandomNumberGenerator.GetBytes(65_021);
            await File.WriteAllBytesAsync(path, bytes, token);
            if (!CreateHardLinkW(WindowsFilePaths.ToExtendedPath(alias), WindowsFilePaths.ToExtendedPath(path), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var cloud = new CloudObject("linked-copy", "linked-original.bin", bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
            var error = await Assert.ThrowsExceptionAsync<COMException>(() => service.MarkInSyncAsync(path, cloud, token));
            Assert.AreEqual(unchecked((int)0x8007018C), error.HResult,
                "HardlinkPolicy.None must reject incompatible aliases rather than convert or unlink them.");
            Assert.IsFalse(service.IsPlaceholder(path));
            Assert.IsFalse(service.IsPlaceholder(alias));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(alias, token));
            using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle.DangerousGetHandle(), out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Assert.AreEqual(2u, information.Links, "A native marking failure must not break the user's hard links.");
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task RestartRetainsUnpinnedCachedFilesAndHonorsPersistedPinnedIntent()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var cached = Path.Combine(root, "cached.bin"); var pinned = Path.Combine(root, "pinned.bin");
            var bytes = RandomNumberGenerator.GetBytes(120_029);
            versions["restart"] = bytes;
            var cloud = new CloudObject("restart", "cached.bin", bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)), DateTimeOffset.UtcNow);
            await service.CreateOrUpdateAsync(cached, cloud, true, token);
            SetWindowsPin(cached, 1);
            await UntilAsync(() => service.IsHydrated(cached), token);
            SetWindowsPin(cached, 2);
            await UntilAsync(() => !service.IsHydrated(cached), token);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(cached, token));
            await service.CreateOrUpdateAsync(pinned, cloud with { Key = "pinned.bin" }, true, token);
            await service.DisconnectAsync();
            // A normal read can clear UNPINNED on some Windows builds. Restore explicit intent
            // while the provider is disconnected to exercise persisted cached UNPINNED state.
            SetWindowsPin(cached, 2);
            Assert.IsTrue(service.IsHydrated(cached));
            SetWindowsPin(pinned, 1);
            await service.ConnectAsync(root, Identity(root), CreateHydrator(versions), token);
            await UntilAsync(() => service.IsHydrated(pinned), token);
            await Task.Delay(300, token);
            Assert.IsTrue(service.IsHydrated(cached), "Restart must keep clean UNPINNED cache for Windows Storage Sense instead of evicting it immediately.");
            Assert.AreEqual(2u, GetPin(cached));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(pinned, token));

            SetWindowsPin(root, 2, recurse: true);
            await UntilAsync(() => !service.IsHydrated(cached) && !service.IsHydrated(pinned), token);
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task NativeFolderPinDownloadsSmallFilesInParallelWithinConfiguredLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0;
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            service.ConfigureTransferLimits(2);
            var folder = Path.Combine(root, "batch");
            var bytes = RandomNumberGenerator.GetBytes(16_037);
            versions["batch"] = bytes;
            for (var index = 0; index < 6; index++)
                await service.CreateOrUpdateAsync(Path.Combine(folder, $"file-{index}.bin"),
                    new CloudObject("batch", $"batch/file-{index}.bin", bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)), DateTimeOffset.UtcNow), true, token);
            var pin = service.SetPinAsync(folder, PinMode.AlwaysAvailable, token);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(12), token); }
            finally { release.TrySetResult(); }
            await pin;
            Assert.AreEqual(2, maximum, "Native small-file downloads must overlap without exceeding the user-selected limit.");
            for (var index = 0; index < 6; index++)
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, $"file-{index}.bin"), token));
        }, versions => async (cloud, offset, length, destination, token) =>
        {
            var count = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref maximum); }
            while (count > observed && Interlocked.CompareExchange(ref maximum, count, observed) != observed);
            if (count >= 2) entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                await destination.WriteAsync(versions[cloud.FileId].AsMemory(checked((int)offset), checked((int)length)), token);
            }
            finally { Interlocked.Decrement(ref active); }
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ResidentCloudIniAndIconsRemainReadableWithoutRecallingOnlineOnlyContent()
    {
        var downloads = 0;
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var folder = Path.Combine(root, "customized");
            var ini = Path.Combine(folder, "desktop.ini"); var iconPath = Path.Combine(folder, "custom.ico");
            var text = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(
                "[.ShellClassInfo]\r\nIconResource=custom.ico,0\r\nInfoTip=Retained customization\r\n")).ToArray();
            // A bounded one-pixel Windows ICO: one BITMAPINFOHEADER, BGRA pixel and AND mask.
            var ico = new byte[70];
            ico[2] = 1; ico[4] = 1; ico[6] = 1; ico[7] = 1; ico[10] = 1; ico[12] = 32;
            ico[14] = 48; ico[18] = 22; ico[22] = 40; ico[26] = 1; ico[30] = 2;
            ico[34] = 1; ico[36] = 32; ico[62] = 180; ico[63] = 90; ico[64] = 20; ico[65] = 255;
            versions["ini"] = text; versions["ico"] = ico;
            await service.CreateOrUpdateAsync(ini, new("ini", "customized/desktop.ini", text.Length,
                Convert.ToHexString(SHA1.HashData(text)), DateTimeOffset.UtcNow), true, token);
            await service.CreateOrUpdateAsync(iconPath, new("ico", "customized/custom.ico", ico.Length,
                Convert.ToHexString(SHA1.HashData(ico)), DateTimeOffset.UtcNow), true, token);

            Assert.IsNull(FolderAppearance.GetIconResource(folder));
            Assert.AreEqual(0, downloads, "Reading appearance must never hydrate an online-only INI.");
            await service.HydrateAsync(ini, token);
            Assert.IsNull(FolderAppearance.GetIconResource(folder));
            Assert.AreEqual(1, downloads, "A cached INI must not implicitly fetch an online-only icon.");
            await service.HydrateAsync(iconPath, token);
            var resource = FolderAppearance.GetIconResource(folder);
            Assert.IsNotNull(resource, "A fully resident Cloud Files INI and icon must preserve the original customization.");
            Assert.AreEqual(iconPath, resource.Path);
            using (var icon = FolderIconResourceReader.Create(resource, 48)) Assert.IsNotNull(icon);
            CollectionAssert.AreEqual(text, await File.ReadAllBytesAsync(ini, token));
            Assert.AreEqual(2, downloads);

            SetWindowsPin(iconPath, 2);
            await UntilAsync(() => !service.IsHydrated(iconPath), token);
            Assert.IsNull(FolderAppearance.GetIconResource(folder));
            Assert.AreEqual(2, downloads, "Later icon eviction must restore a fallback without any background recall.");
        }, versions => async (cloud, offset, length, destination, token) =>
        {
            Interlocked.Increment(ref downloads);
            await destination.WriteAsync(versions[cloud.FileId].AsMemory(checked((int)offset), checked((int)length)), token);
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task InterruptedNativeDownloadResumesResidentPrefixAfterProviderReconnect()
    {
        const int prefix = 1024 * 1024;
        var firstBytes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOffsets = new ConcurrentQueue<long>();
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "interrupted.bin");
            var content = RandomNumberGenerator.GetBytes(3 * prefix + 19);
            versions["interrupted"] = content;
            await service.CreateOrUpdateAsync(path, new("interrupted", "interrupted.bin", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            var pendingRead = Task.Run(() => File.ReadAllBytes(path), token);
            await firstBytes.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            await service.DisconnectAsync();
            await Assert.ThrowsExceptionAsync<IOException>(async () => { _ = await pendingRead; });
            Assert.IsFalse(service.IsHydrated(path), DescribeNativeCache(path));
            await service.ConnectAsync(root, Identity(root), async (cloud, offset, length, destination, cancellation) =>
            {
                resumedOffsets.Enqueue(offset);
                await destination.WriteAsync(content.AsMemory(checked((int)offset), checked((int)length)), cancellation);
            }, token);
            CollectionAssert.AreEqual(content, await Task.Run(() => File.ReadAllBytes(path), token));
            Assert.IsTrue(resumedOffsets.Count > 0);
            Assert.IsTrue(resumedOffsets.All(offset => offset >= prefix),
                "An interrupted native hydration must resume missing ranges instead of downloading its accepted resident prefix again. Offsets: " + string.Join(",", resumedOffsets));
        }, versions => async (cloud, offset, length, destination, token) =>
        {
            await destination.WriteAsync(versions[cloud.FileId].AsMemory(checked((int)offset), prefix), token);
            firstBytes.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ChecksumRejectedNativePrefixCannotSurviveAsValidResumedContent()
    {
        var calls = new ConcurrentQueue<long>();
        var attempts = 0;
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "rejected-prefix.bin");
            var content = RandomNumberGenerator.GetBytes(1024 * 1024 + 19);
            versions["rejected-prefix"] = content;
            await service.CreateOrUpdateAsync(path, new("rejected-prefix", "rejected-prefix.bin", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            try
            {
                var initial = await Task.Run(() => File.ReadAllBytes(path), token);
                CollectionAssert.AreEqual(content, initial, "Automatic recovery must never release rejected bytes. Offsets: " + string.Join(",", calls));
            }
            catch (IOException) { /* A failed read may be retried explicitly after a checksum rejection. */ }
            CollectionAssert.AreEqual(content, await Task.Run(() => File.ReadAllBytes(path), token),
                "A rejected prefix must be discarded before a tail-only native resume can make the file readable.");
            Assert.IsTrue(calls.Skip(1).Contains(0), "A checksum rejection must cause a complete native restart, not reuse rejected cached ranges. Offsets: " + string.Join(",", calls));
            Assert.IsTrue(attempts is >= 2 and <= 3, "Checksum recovery must stay bounded.");
        }, versions => async (cloud, offset, length, destination, token) =>
        {
            calls.Enqueue(offset);
            var attempt = Interlocked.Increment(ref attempts);
            var data = versions[cloud.FileId].AsMemory(checked((int)offset), checked((int)length)).ToArray();
            if (attempt == 1)
            {
                data[0] ^= 0xff;
                await destination.WriteAsync(data, token);
                throw new InvalidDataException("Simulated whole-file checksum rejection after a corrupt resident prefix.");
            }
            await destination.WriteAsync(data, token);
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task RepeatedNativeChecksumFailuresDiscardCacheAndStopAfterOneRetry()
    {
        var attempts = 0;
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "always-rejected.bin");
            var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 19);
            versions["always-rejected"] = bytes;
            await service.CreateOrUpdateAsync(path, new("always-rejected", "always-rejected.bin", bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)), DateTimeOffset.UtcNow), true, token);
            await Assert.ThrowsExceptionAsync<IOException>(async () => { _ = await Task.Run(() => File.ReadAllBytes(path), token); });
            Assert.AreEqual(2, attempts, "Persistent checksum rejection must stop after one automatic full retry.");
            Assert.IsFalse(service.IsHydrated(path), "Rejected prefix bytes must be removed even after the last allowed retry.");
        }, versions => async (cloud, offset, length, destination, token) =>
        {
            Interlocked.Increment(ref attempts);
            var content = versions[cloud.FileId].AsMemory(checked((int)offset), checked((int)length)).ToArray();
            content[0] ^= 0xff;
            await destination.WriteAsync(content, token);
            throw new InvalidDataException("Simulated persistent checksum mismatch.");
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ResumedNativeCacheValidatesRetainedPrefixBeforeAcknowledgingUserRead()
    {
        const int prefix = 1024 * 1024;
        var firstBytes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOffsets = new ConcurrentQueue<long>();
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "damaged-resident-prefix.bin");
            var content = RandomNumberGenerator.GetBytes(3 * prefix + 19);
            versions["damaged-prefix"] = content;
            await service.CreateOrUpdateAsync(path, new("damaged-prefix", "damaged-resident-prefix.bin", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            var pendingRead = Task.Run(() => File.ReadAllBytes(path), token);
            await firstBytes.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            await service.DisconnectAsync();
            await Assert.ThrowsExceptionAsync<IOException>(async () => { _ = await pendingRead; });
            await service.ConnectAsync(root, Identity(root), async (cloud, offset, length, destination, cancellation) =>
            {
                resumedOffsets.Enqueue(offset);
                await destination.WriteAsync(content.AsMemory(checked((int)offset), checked((int)length)), cancellation);
            }, token);
            CollectionAssert.AreEqual(content, await Task.Run(() => File.ReadAllBytes(path), token),
                "Native EOF must stay unavailable until the retained prefix and new tail pass assembled-cache SHA1 verification.");
            Assert.IsTrue(resumedOffsets.TryPeek(out var firstOffset) && firstOffset >= prefix,
                "The initial resumed transfer must retrieve the missing tail, preserving network resume.");
            Assert.IsTrue(resumedOffsets.Skip(1).Contains(0),
                "A corrupt retained prefix must trigger a complete cache restart after its assembled SHA1 fails.");
        }, versions => async (cloud, offset, length, destination, token) =>
        {
            // Model corruption in accepted prefix bytes before interruption. There is no
            // completed transport checksum to reject these bytes until the resumed file is assembled.
            var data = versions[cloud.FileId].AsMemory(checked((int)offset), prefix).ToArray();
            data[0] ^= 0xff;
            await destination.WriteAsync(data, token);
            firstBytes.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task NativeReadinessWaitsForValidationAndKeepsSmallLocalEditsAvailable()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "pending-validation.bin");
            var bytes = RandomNumberGenerator.GetBytes(512 * 1024 + 19);
            versions["pending-validation"] = bytes;
            await service.CreateOrUpdateAsync(path, new("pending-validation", "pending-validation.bin", bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)), DateTimeOffset.UtcNow), true, token);
            Task<byte[]> read;
            await TransferResources.NativeValidation.WaitAsync(token);
            try
            {
                read = Task.Run(() => File.ReadAllBytes(path), token);
                await UntilAsync(() => NativeOnDiskRangeCovers(path, bytes.Length), token);
                Assert.IsFalse(service.IsHydrated(path), "Full resident bytes must remain unavailable while their validation ACK is pending. " + DescribeNativeCache(path));
                Assert.IsFalse(read.IsCompleted, "Windows must keep the user's read pending until native validation completes.");
                var appearanceProbe = Task.Run(() =>
                {
                    try { using var handle = FolderAppearance.OpenResidentFile(path); return false; }
                    catch (IOException) { return true; }
                }, token);
                Assert.IsTrue(await appearanceProbe.WaitAsync(TimeSpan.FromSeconds(2), token),
                    "Folder appearance must promptly reject unvalidated resident bytes without waiting for the native ACK.");
            }
            finally { TransferResources.NativeValidation.Release(); }
            CollectionAssert.AreEqual(bytes, await read);
            Assert.IsTrue(service.IsHydrated(path), DescribeNativeCache(path));
            await using (var appearance = new FileStream(FolderAppearance.OpenResidentFile(path), FileAccess.Read))
            {
                var residentBytes = new byte[bytes.Length];
                await appearance.ReadExactlyAsync(residentBytes, token);
                CollectionAssert.AreEqual(bytes, residentBytes, "Validated appearance metadata must remain readable on its held no-recall handle.");
            }

            var edited = bytes.ToArray();
            edited[17] ^= 0xff;
            await using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                writer.Position = 17;
                await writer.WriteAsync(edited.AsMemory(17, 1), token);
                await writer.FlushAsync(token);
            }
            Assert.IsTrue(service.HasLocalChanges(path), "A small write must remain a local edit.");
            Assert.IsTrue(service.IsHydrated(path), "Validated and locally modified range coverage must keep the whole file available. " + DescribeNativeCache(path));
            CollectionAssert.AreEqual(edited, await File.ReadAllBytesAsync(path, token));
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task NativeValidationPreservesDirtyLocalContentAcrossProviderReconnect()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "local-edit.bin");
            var original = RandomNumberGenerator.GetBytes(128_019);
            versions["local-original"] = original;
            await service.CreateOrUpdateAsync(path, new("local-original", "local-edit.bin", original.Length,
                Convert.ToHexString(SHA1.HashData(original)), DateTimeOffset.UtcNow), true, token);
            await service.HydrateAsync(path, token);
            var edited = original.Concat(new byte[] { 5, 6, 7 }).ToArray();
            await File.WriteAllBytesAsync(path, edited, token);
            await service.DisconnectAsync();
            await service.ConnectAsync(root, Identity(root), CreateHydrator(versions), token);
            CollectionAssert.AreEqual(edited, await Task.Run(() => File.ReadAllBytes(path), token),
                "Validation must retain unsent local edits instead of comparing them with an older cloud version.");
            Assert.IsTrue(service.HasLocalChanges(path));
        });
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task LegacyNativeVersionWithoutSha1RetainsImmutableTransportFallback()
    {
        await RunIsolatedAsync(async (service, root, versions, token) =>
        {
            var path = Path.Combine(root, "legacy-version.bin");
            var bytes = RandomNumberGenerator.GetBytes(65_019);
            versions["legacy"] = bytes;
            await service.CreateOrUpdateAsync(path, new("legacy", "legacy-version.bin", bytes.Length,
                null, DateTimeOffset.UtcNow), true, token);
            CollectionAssert.AreEqual(bytes, await Task.Run(() => File.ReadAllBytes(path), token));
        });
    }

    private static async Task RunIsolatedAsync(Func<WindowsPlaceholderService, string, Dictionary<string, byte[]>, CancellationToken, Task> scenario,
        Func<Dictionary<string, byte[]>, HydrationHandler>? createHydrator = null)
    {
        var parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(parent, "CloudInlet-NativeIntent-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var versions = new Dictionary<string, byte[]>();
        await using var service = new WindowsPlaceholderService();
        try
        {
            await service.ConnectAsync(root, Identity(root), (createHydrator ?? CreateHydrator)(versions), timeout.Token);
            // Test roots must not inherit this machine's personal-folder Always keep setting.
            // Each scenario below deliberately sets its own native Shell intent.
            SetWindowsPin(root, 0);
            await scenario(service, root, versions, timeout.Token);
        }
        finally
        {
            await service.DisconnectAsync();
            if (service.RegistrationId is { } id) StorageProviderSyncRootManager.Unregister(id);
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CloudInlet-NativeIntent-", StringComparison.Ordinal))
                throw new IOException("Refusing to remove a folder outside this generated native test root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static string Identity(string root) => "native-intent-" + Path.GetFileName(root);
    private static HydrationHandler CreateHydrator(Dictionary<string, byte[]> versions) => async (cloud, offset, length, destination, token) =>
    {
        var content = versions[cloud.FileId];
        await destination.WriteAsync(content.AsMemory(checked((int)offset), checked((int)length)), token);
        if (cloud.FileId == "bad-after-write") throw new IOException("Simulated transport checksum rejection after receiving aligned EOF.");
    };

    private static void SetWindowsPin(string path, uint pin, bool recurse = false)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Marshal.ThrowExceptionForHR(CfSetPinState(handle, pin, recurse ? 5u : 0u, IntPtr.Zero));
    }

    private static uint GetPin(string path)
    {
        // A normal directory inside a sync root can hold Shell pin intent without itself
        // being a CF placeholder. Its native PINNED/UNPINNED attributes remain authoritative.
        var attributes = (uint)File.GetAttributes(path);
        return (attributes & 0x80000u) != 0 ? 1u : (attributes & 0x100000u) != 0 ? 2u : 0u;
    }

    private static string DescribeNativeCache(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return $"Attribute handle failed: {Marshal.GetLastWin32Error()}.";
        if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var file))
            return $"File information failed: {Marshal.GetLastWin32Error()}.";
        var state = State(path);
        var onDiskResult = CfGetPlaceholderRangeInfo(handle, 1, 0, file.Size, out var onDisk, 16, out var onDiskBytes);
        var validatedResult = CfGetPlaceholderRangeInfo(handle, 2, 0, file.Size, out var validated, 16, out var validatedBytes);
        var modifiedResult = CfGetPlaceholderRangeInfo(handle, 3, 0, file.Size, out var modified, 16, out var modifiedBytes);
        return $"State={state}, size={file.Size}; ONDISK=0x{onDiskResult:X8}, bytes={onDiskBytes}, {onDisk.Offset}+{onDisk.Length}; " +
            $"VALIDATED=0x{validatedResult:X8}, bytes={validatedBytes}, {validated.Offset}+{validated.Length}; " +
            $"MODIFIED=0x{modifiedResult:X8}, bytes={modifiedBytes}, {modified.Offset}+{modified.Length}.";
    }

    private static bool NativeOnDiskRangeCovers(string path, long size)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        return !handle.IsInvalid && CfGetPlaceholderRangeInfo(handle, 1, 0, size, out var range, 16, out var returned) == 0 &&
            returned == 16 && range.Offset == 0 && range.Length >= size;
    }

    private static void ClearGeneratedFixtureReadOnly(string path)
    {
        // These paths live only under RunIsolatedAsync's guarded GUID test root. Production
        // code never clears attributes; this merely permits disposal of the test fixture.
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        uint securityInformation, byte[] securityDescriptor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existingPath, IntPtr security);

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail("Windows did not finish the requested availability change.");
            await Task.Delay(25, token);
        }
    }
}
