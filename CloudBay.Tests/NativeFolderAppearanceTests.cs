using System.Text;
using System.Runtime.InteropServices;
using CloudBay.Core.Sync;
using CloudBay.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string path);

    private static async Task IsolatedAsync(Func<string, Task> test)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudBay.Appearance.Tests"));
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
