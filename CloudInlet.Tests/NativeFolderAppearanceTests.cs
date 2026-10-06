using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using CloudInlet.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Provider;

namespace CloudInlet.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeFolderAppearanceTests
{
    [TestMethod]
    public async Task SourceCustomizationIsPreservedWithoutOverwritingIniContents()
    {
        await IsolatedAsync(async root =>
        {
            var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            var icon = Path.Combine(source, "custom.ico");
            await File.WriteAllBytesAsync(icon, new byte[] { 0, 0, 1, 0 });
            var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
                "; custom comment\r\n[.ShellClassInfo]\r\nIconResource=custom.ico,0\r\nInfoTip=Personal folder\r\n[Other]\r\nKeep=Yes\r\n")).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(source, "desktop.ini"), original);
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            File.SetAttributes(Path.Combine(source, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
            await VerifiedTreeCopy.CopyAsync(source, destination);
            await FolderAppearance.PreserveAsync(source, destination);
            await FolderAppearance.EnsureIconAsync(destination, new("C:\\Windows\\System32\\imageres.dll", -3));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(destination, "desktop.ini")));
            var resource = FolderAppearance.GetIconResource(destination);
            Assert.IsNotNull(resource);
            Assert.AreEqual(Path.Combine(destination, "custom.ico"), resource.Path);
            Assert.AreEqual(0, resource.Index);
            Assert.IsTrue((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0);
            Assert.AreEqual(FileAttributes.Hidden | FileAttributes.System,
                File.GetAttributes(Path.Combine(destination, "desktop.ini")) & (FileAttributes.Hidden | FileAttributes.System));
        });
    }

    [TestMethod]
    public async Task KnownFolderFallbackCreatesUnicodeIniOnlyWhenMissing()
    {
        await IsolatedAsync(async root =>
        {
            var fallback = FolderAppearance.GetKnownFolderIcon("Music");
            Assert.IsNotNull(fallback, "The Windows Music folder definition must expose its own icon resource.");
            await FolderAppearance.EnsureIconAsync(root, fallback);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "desktop.ini"));
            CollectionAssert.AreEqual(Encoding.Unicode.GetPreamble(), bytes[..2]);
            var text = await File.ReadAllTextAsync(Path.Combine(root, "desktop.ini"));
            StringAssert.Contains(text, "IconResource=" + fallback.Path + "," + fallback.Index);
            await FolderAppearance.EnsureIconAsync(root, new("C:\\Windows\\System32\\shell32.dll", 3));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "desktop.ini")), "Retries must not reset customized appearance.");
        });
    }

    [TestMethod]
    public async Task MissingAndNetworkIconResourcesReturnAFallbackWithoutAccess()
    {
        await IsolatedAsync(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "desktop.ini"), "[.ShellClassInfo]\r\nIconResource=missing.ico,0\r\n");
            Assert.IsNull(FolderAppearance.GetIconResource(root), "Missing icons must fall back without a content read or network request.");
            Assert.IsNull(FolderAppearance.GetIconResource("\\\\server\\share\\folder"), "Icon lookup must not contact a network share.");
        });
    }

    [TestMethod]
    public async Task LocalDllAndExeIconsAreResourcesWithoutExecutingOrLoadingPrograms()
    {
        await IsolatedAsync(async root =>
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var original = new[] { Path.Combine(windows, "SystemResources", "imageres.dll.mun"),
                Path.Combine(windows, "System32", "moricons.dll") }.First(File.Exists);
            foreach (var extension in new[] { ".dll", ".exe" })
            {
                var path = Path.Combine(root, "custom" + extension);
                File.Copy(original, path);
                await File.WriteAllTextAsync(Path.Combine(root, "desktop.ini"), $"[.ShellClassInfo]\r\nIconResource=custom{extension},0\r\n");
                var resource = FolderAppearance.GetIconResource(root);
                Assert.IsNotNull(resource);
                Assert.AreEqual(IntPtr.Zero, GetModuleHandleW(path));
                using var icon = FolderIconResourceReader.Create(resource, 48);
                Assert.IsNotNull(icon, "Supported local DLL and EXE customization must retain its icon.");
                Assert.IsFalse(icon.IsInvalid);
                Assert.AreEqual(IntPtr.Zero, GetModuleHandleW(path), "A resource-only mapping must never become an executable module.");
            }
        });
    }

    [TestMethod]
    public async Task MalformedIconResourcesReturnFallbackWithoutUnboundedAllocation()
    {
        await IsolatedAsync(async root =>
        {
            var path = Path.Combine(root, "broken.ico");
            await File.WriteAllBytesAsync(path, new byte[] { 0, 0, 1, 0, 255, 255 });
            using var icon = FolderIconResourceReader.Create(new(path, 0), 48);
            Assert.IsNull(icon);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000)]
    public async Task NativeDesktopIniSurvivesVerifiedCopyBackAndRejectsLateSourceEdits(bool editAfterCopy)
    {
        await NativeIsolatedAsync(async (service, root, versions, token) =>
        {
            var source = Path.Combine(root, "Pictures"); var destination = Path.Combine(root, "original-Pictures");
            var originalIni = Path.Combine(source, "desktop.ini");
            var content = CustomIni();
            versions["pictures-appearance"] = content;
            await service.CreateOrUpdateAsync(originalIni, new CloudObject("pictures-appearance", "Pictures/desktop.ini", content.Length,
                Convert.ToHexString(SHA1.HashData(content)), DateTimeOffset.UtcNow), true, token);
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly | FileAttributes.System);
            File.SetAttributes(originalIni, File.GetAttributes(originalIni) | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            Assert.IsFalse(service.IsHydrated(originalIni));
            var sourceReport = Path.Combine(source, "report.txt");
            await File.WriteAllTextAsync(sourceReport, "alpha", token);
            var originalReportTime = File.GetLastWriteTimeUtc(sourceReport);

            // This is the copy-and-appearance sequence used by KnownFolderBackup.DisableAsync,
            // without altering any of the user's actual Known Folder mappings.
            var verifiedSource = await VerifiedTreeCopy.CopyVerifiedAsync(source, destination, token);
            Assert.IsTrue(service.IsPlaceholder(originalIni), "Hydrating a verified restore source retains its Cloud Files reparse tag.");
            Assert.IsTrue(service.IsHydrated(originalIni));
            await FolderAppearance.PreserveAfterVerifiedCopyAsync(source, destination, token);
            await FolderAppearance.PreserveAfterVerifiedCopyAsync(source, destination, token);
            if (editAfterCopy)
            {
                await File.WriteAllTextAsync(sourceReport, "bravo", token);
                File.SetLastWriteTimeUtc(sourceReport, originalReportTime);
                var error = Assert.ThrowsException<IOException>(() => VerifiedTreeCopy.EnsureUnchanged(source, verifiedSource, token));
                StringAssert.Contains(error.Message, "source folder changed");
            }
            else VerifiedTreeCopy.EnsureUnchanged(source, verifiedSource, token);
            var copiedIni = Path.Combine(destination, "desktop.ini");
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(copiedIni, token));
            Assert.AreEqual(editAfterCopy ? "bravo" : "alpha", await File.ReadAllTextAsync(sourceReport, token), "The current source remains intact if the mapping guard rejects a later edit.");
            Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt"), token), "The verified copy remains intact if the mapping guard rejects a later edit.");
            Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System,
                File.GetAttributes(copiedIni) & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.System,
                File.GetAttributes(destination) & (FileAttributes.ReadOnly | FileAttributes.System));
            Assert.IsNotNull(FolderAppearance.GetIconResource(destination));
        });
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(90_000)]
    public async Task AppearanceAttributesAcceptOnlineOnlyMetadataWithoutRecallingEitherSide(bool cloudSource, bool cloudDestination)
    {
        await NativeIsolatedAsync(async (service, root, versions, token) =>
        {
            var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
            var sourceIni = Path.Combine(source, "desktop.ini"); var destinationIni = Path.Combine(destination, "desktop.ini");
            var original = CustomIni();
            foreach (var item in new[] { ("source", source, sourceIni, cloudSource), ("destination", destination, destinationIni, cloudDestination) })
            {
                Directory.CreateDirectory(item.Item2);
                if (item.Item4)
                {
                    versions[item.Item1] = original;
                    await service.CreateOrUpdateAsync(item.Item3, new CloudObject(item.Item1, item.Item1 + "/desktop.ini", original.Length,
                        Convert.ToHexString(SHA1.HashData(original)), DateTimeOffset.UtcNow), true, token);
                    Assert.IsFalse(service.IsHydrated(item.Item3));
                }
                else await File.WriteAllBytesAsync(item.Item3, original, token);
            }
            File.SetAttributes(sourceIni, File.GetAttributes(sourceIni) | FileAttributes.ReadOnly);
            await FolderAppearance.PreserveAsync(source, destination, token);
            await FolderAppearance.RefreshLocalBackupAppearanceAsync(source, destination, new("C:\\Windows\\System32\\shell32.dll", 3), token);
            Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System,
                File.GetAttributes(destinationIni) & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            if (cloudSource)
            {
                Assert.IsNull(FolderAppearance.GetIconResource(source));
                Assert.IsFalse(service.IsHydrated(sourceIni), "Source attributes and icon discovery must not download its INI.");
            }
            else CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(sourceIni, token));
            if (cloudDestination)
            {
                Assert.IsNull(FolderAppearance.GetIconResource(destination));
                Assert.IsFalse(service.IsHydrated(destinationIni), "Repairing activation attributes must not replace or download authoritative cloud metadata.");
            }
            else CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(destinationIni, token));
        });
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ActualMetadataSymbolicLinksAreRejectedBeforeChangingDestinationAppearance(bool sourceLink)
    {
        await IsolatedAsync(async root =>
        {
            var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly | FileAttributes.System);
            var outside = Path.Combine(root, "retained-user-metadata.ini");
            var original = CustomIni();
            await File.WriteAllBytesAsync(outside, original);
            var linkedIni = Path.Combine(sourceLink ? source : destination, "desktop.ini");
            var regularIni = Path.Combine(sourceLink ? destination : source, "desktop.ini");
            await File.WriteAllBytesAsync(regularIni, original);
            var originalAttributes = File.GetAttributes(outside);
            var destinationAttributes = File.GetAttributes(destination);
            File.CreateSymbolicLink(linkedIni, outside);
            try
            {
                var error = await Assert.ThrowsExceptionAsync<IOException>(() => FolderAppearance.PreserveAsync(source, destination));
                StringAssert.Contains(error.Message, "linked metadata");
                if (!sourceLink)
                    await Assert.ThrowsExceptionAsync<IOException>(() => FolderAppearance.EnsureIconAsync(destination, new("C:\\Windows\\System32\\shell32.dll", 3)));
                Assert.IsNull(FolderAppearance.GetIconResource(sourceLink ? source : destination));
                Assert.AreEqual(originalAttributes, File.GetAttributes(outside), "Appearance repair must not change a symbolic link's target.");
                Assert.AreEqual(destinationAttributes, File.GetAttributes(destination), "Reject unsafe metadata before changing the destination folder.");
                CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(outside));
            }
            finally { File.Delete(linkedIni); }
        });
    }

    [TestMethod]
    public async Task AppearanceAndIconMetadataWorkBeyondTheNativeMaxPathLimit()
    {
        await IsolatedAsync(async root =>
        {
            var nested = root;
            while (nested.Length < 310) nested = Path.Combine(nested, "valid-personal-folder-segment");
            var source = Path.Combine(nested, "source"); var destination = Path.Combine(nested, "destination");
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            var content = CustomIni();
            await File.WriteAllBytesAsync(Path.Combine(source, "desktop.ini"), content);
            await File.WriteAllBytesAsync(Path.Combine(destination, "desktop.ini"), content);
            await FolderAppearance.PreserveAsync(source, destination);
            await FolderAppearance.EnsureIconAsync(destination, new("C:\\Windows\\System32\\shell32.dll", 3));
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(Path.Combine(destination, "desktop.ini")));
            Assert.IsNotNull(FolderAppearance.GetIconResource(destination), "The no-recall native metadata reader must also accept a long folder path.");
        });
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(10_000)]
    public async Task PostVerifiedCopyAppearanceNeverOpensAnUnavailableUncSourceOrDestination(bool networkSource)
    {
        await IsolatedAsync(async root =>
        {
            var local = Path.Combine(root, "local");
            Directory.CreateDirectory(local);
            var localIni = Path.Combine(local, "desktop.ini");
            var content = CustomIni();
            await File.WriteAllBytesAsync(localIni, content);
            File.SetAttributes(localIni, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(local, FileAttributes.Directory | FileAttributes.ReadOnly);
            var folderAttributes = File.GetAttributes(local); var iniAttributes = File.GetAttributes(localIni);
            const string unavailableShare = @"\\CloudInlet-Appearance-Tests.invalid\unavailable\Pictures";
            var task = FolderAppearance.PreserveAfterVerifiedCopyAsync(networkSource ? unavailableShare : local,
                networkSource ? local : unavailableShare);
            Assert.IsTrue(task.IsCompletedSuccessfully, "UNC cosmetic work must complete immediately without starting file-system I/O.");
            await task;
            Assert.AreEqual(folderAttributes, File.GetAttributes(local));
            Assert.AreEqual(iniAttributes, File.GetAttributes(localIni));
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(localIni));
        });
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task StartupAppearanceRepairsOnlyTheLocalBackupWhenTheOriginalIsAnUnavailableShare()
    {
        await IsolatedAsync(async root =>
        {
            const string unavailableShare = @"\\CloudInlet-Appearance-Tests.invalid\unavailable\Pictures";
            await FolderAppearance.RefreshLocalBackupAppearanceAsync(unavailableShare, root, new("C:\\Windows\\System32\\shell32.dll", 3));
            var ini = Path.Combine(root, "desktop.ini");
            var first = await File.ReadAllBytesAsync(ini);
            StringAssert.Contains(await File.ReadAllTextAsync(ini), "IconResource=C:\\Windows\\System32\\shell32.dll,3");
            await FolderAppearance.RefreshLocalBackupAppearanceAsync(unavailableShare, root, new("C:\\Windows\\System32\\shell32.dll", 4));
            CollectionAssert.AreEqual(first, await File.ReadAllBytesAsync(ini), "An existing local customization remains authoritative during startup repair.");
            Assert.IsTrue((File.GetAttributes(root) & FileAttributes.ReadOnly) != 0);
            Assert.AreEqual(FileAttributes.Hidden | FileAttributes.System,
                File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System));
        });
    }

    private static byte[] CustomIni()
    {
        var resource = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "shell32.dll");
        return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
            "; retained user customization\r\n[.ShellClassInfo]\r\nIconResource=" + resource + ",3\r\nInfoTip=Personal folder\r\n[Other]\r\nKeep=Yes\r\n")).ToArray();
    }

    private static async Task NativeIsolatedAsync(Func<WindowsPlaceholderService, string, Dictionary<string, byte[]>, CancellationToken, Task> test)
    {
        var parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var root = Path.Combine(parent, "CloudInlet-NativeAppearance-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var versions = new Dictionary<string, byte[]>();
        await using var service = new WindowsPlaceholderService();
        try
        {
            await service.ConnectAsync(root, "native-appearance-" + Path.GetFileName(root), async (cloud, offset, length, destination, token) =>
                await destination.WriteAsync(versions[cloud.FileId].AsMemory(checked((int)offset), checked((int)length)), token), timeout.Token);
            using (var handle = CloudFilesNative.CreateFileW(root, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Marshal.ThrowExceptionForHR(CloudFilesNative.CfSetPinState(handle, 0, 0, IntPtr.Zero));
            }
            await test(service, root, versions, timeout.Token);
        }
        finally
        {
            await service.DisconnectAsync();
            if (service.RegistrationId is { } id) StorageProviderSyncRootManager.Unregister(id);
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CloudInlet-NativeAppearance-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(resolved)["CloudInlet-NativeAppearance-".Length..], "N", out _))
                throw new IOException("Refusing cleanup outside a generated native folder appearance test root.");
            if (Directory.Exists(resolved))
            {
                foreach (var path in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                foreach (var directory in Directory.EnumerateDirectories(resolved, "*", SearchOption.AllDirectories))
                    File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
                File.SetAttributes(resolved, File.GetAttributes(resolved) & ~FileAttributes.ReadOnly);
                Directory.Delete(resolved, true);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string path);

    private static async Task IsolatedAsync(Func<string, Task> test)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudInlet.Appearance.Tests"));
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                throw new IOException("Refusing cleanup outside a generated folder appearance test root.");
            foreach (var path in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            foreach (var directory in Directory.EnumerateDirectories(resolved, "*", SearchOption.AllDirectories))
                File.SetAttributes(directory, FileAttributes.Directory);
            File.SetAttributes(resolved, FileAttributes.Directory);
            Directory.Delete(resolved, true);
        }
    }
}
