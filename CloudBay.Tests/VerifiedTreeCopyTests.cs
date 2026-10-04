using CloudBay.Core.Sync;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class VerifiedTreeCopyTests
{
    [TestMethod]
    public async Task NamedMetadataStreamsIncludingDownloadOriginAndFolderMetadataSurviveVerifiedCopyAndRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var file = Path.Combine(source, "download.txt");
        const string origin = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.test/download\r\n";
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(file, "download bytes");
            await File.WriteAllTextAsync(file + ":Zone.Identifier:$DATA", origin);
            await File.WriteAllTextAsync(file + ":application.metadata:$DATA", "keep custom metadata");
            await File.WriteAllTextAsync(source + ":catalog:$DATA", "folder tags");
            var inspection = VerifiedTreeCopy.Inspect(source);
            Assert.AreEqual(1L, inspection.FileCount);
            Assert.AreEqual(new FileInfo(file).Length + System.Text.Encoding.UTF8.GetByteCount(origin + "keep custom metadata" + "folder tags"), inspection.TotalBytes);
            Assert.IsFalse(inspection.HasOnlineOnlyFiles);
            var fingerprint = inspection.Fingerprint;
            await VerifiedTreeCopy.CopyAsync(source, destination);
            await VerifiedTreeCopy.CopyAsync(source, destination);
            Assert.AreEqual("download bytes", await File.ReadAllTextAsync(Path.Combine(destination, "download.txt")));
            Assert.AreEqual(origin, await File.ReadAllTextAsync(Path.Combine(destination, "download.txt") + ":Zone.Identifier:$DATA"));
            Assert.AreEqual("keep custom metadata", await File.ReadAllTextAsync(Path.Combine(destination, "download.txt") + ":application.metadata:$DATA"));
            Assert.AreEqual("folder tags", await File.ReadAllTextAsync(destination + ":catalog:$DATA"));
            Assert.AreEqual(fingerprint, VerifiedTreeCopy.GetFingerprint(source));
            Assert.AreEqual(1, Directory.GetFiles(destination).Length);
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task EqualUnnamedBytesWithDifferentMetadataPreserveBothVersionsAsAConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var sourceFile = Path.Combine(source, "report.txt"); var targetFile = Path.Combine(destination, "report.txt");
        try
        {
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(sourceFile, "same file bytes");
            await File.WriteAllTextAsync(targetFile, "same file bytes");
            await File.WriteAllTextAsync(sourceFile + ":tags:$DATA", "source metadata");
            await File.WriteAllTextAsync(targetFile + ":tags:$DATA", "existing metadata");
            await VerifiedTreeCopy.CopyAsync(source, destination);
            var conflict = Directory.GetFiles(destination, "report (backup conflict *).txt").Single();
            Assert.AreEqual("source metadata", await File.ReadAllTextAsync(targetFile + ":tags:$DATA"));
            Assert.AreEqual("existing metadata", await File.ReadAllTextAsync(conflict + ":tags:$DATA"));
            await VerifiedTreeCopy.CopyAsync(source, destination);
            Assert.AreEqual(1, Directory.GetFiles(destination, "report (backup conflict *).txt").Length);
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task ExistingDirectoryMetadataConflictIsRejectedWithoutOverwritingEitherStream()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "source retained");
            await File.WriteAllTextAsync(source + ":tags:$DATA", "source tags");
            await File.WriteAllTextAsync(destination + ":tags:$DATA", "destination tags");
            var error = await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, destination));
            StringAssert.Contains(error.Message, "Neither metadata stream was overwritten");
            Assert.AreEqual("source tags", await File.ReadAllTextAsync(source + ":tags:$DATA"));
            Assert.AreEqual("destination tags", await File.ReadAllTextAsync(destination + ":tags:$DATA"));
            Assert.AreEqual("source retained", await File.ReadAllTextAsync(Path.Combine(source, "keep.txt")));
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task LateSameSizeEditWithPreservedModificationTimeCannotPassFinalCopyValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var file = Path.Combine(source, "report.txt");
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(file, "alpha");
            var modified = File.GetLastWriteTimeUtc(file);
            var before = VerifiedTreeCopy.GetFingerprint(source);
            var progress = new InlineProgress(relative =>
            {
                if (relative != "report.txt") return;
                File.WriteAllText(file, "bravo");
                File.SetLastWriteTimeUtc(file, modified);
            });
            var error = await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, destination, progress: progress));
            StringAssert.Contains(error.Message, "source folder changed");
            Assert.AreEqual("bravo", await File.ReadAllTextAsync(file));
            Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(file));
            Assert.AreNotEqual(before, VerifiedTreeCopy.GetFingerprint(source));
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task LateNamedStreamEditWithPreservedFileModificationTimeCannotPassFinalCopyValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var file = Path.Combine(source, "report.txt");
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(file, "unchanged main bytes");
            await File.WriteAllTextAsync(file + ":tags:$DATA", "alpha");
            var modified = File.GetLastWriteTimeUtc(file);
            var progress = new InlineProgress(relative =>
            {
                File.WriteAllText(file + ":tags:$DATA", "bravo");
                File.SetLastWriteTimeUtc(file, modified);
            });
            await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, destination, progress: progress));
            Assert.AreEqual("bravo", await File.ReadAllTextAsync(file + ":tags:$DATA"));
            Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt") + ":tags:$DATA"));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(file));
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task MetadataChangeWithIdenticalBytesIsReverifiedAndAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var file = Path.Combine(source, "report.txt");
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(file, "same bytes");
            var modified = File.GetLastWriteTimeUtc(file);
            var progress = new InlineProgress(_ =>
            {
                File.SetLastWriteTimeUtc(file, modified.AddMinutes(1));
                File.SetLastWriteTimeUtc(file, modified);
            });
            var verified = await VerifiedTreeCopy.CopyVerifiedAsync(source, destination, progress: progress);
            Assert.AreEqual(VerifiedTreeCopy.GetFingerprint(source), verified,
                "The returned snapshot must describe the reverified final source metadata, including hydration-like changes.");
            Assert.AreEqual("same bytes", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(file));
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task LongNativePathsAndNearLimitConflictNamesAreCopiedWithoutChangingSourceBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var relative = Path.Combine(new string('a', 110), new string('b', 110), new string('x', 245) + ".txt");
        var file = Path.Combine(source, relative); var target = Path.Combine(destination, relative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(file, "source bytes"); await File.WriteAllTextAsync(target, "existing bytes");
            File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.ReadOnly);
            await VerifiedTreeCopy.CopyAsync(source, destination);
            Assert.AreEqual("source bytes", await File.ReadAllTextAsync(target));
            var conflict = Directory.GetFiles(Path.GetDirectoryName(target)!).Single(path => path != target);
            Assert.IsTrue(Path.GetFileName(conflict).Length <= 255);
            Assert.AreEqual("existing bytes", await File.ReadAllTextAsync(conflict));
            Assert.AreEqual("source bytes", await File.ReadAllTextAsync(file));
            Assert.IsTrue((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            DeleteGeneratedRoot(root);
        }
    }

    [DataTestMethod]
    [DataRow(FileAttributes.Encrypted)]
    [DataRow(FileAttributes.Encrypted | FileAttributes.Directory)]
    public void EfsProtectionRequiresExplicitRejectionInsteadOfADecryptedNativeCopy(FileAttributes attributes)
    {
        // Creating EFS fixture credentials would mutate the real user's certificate store.
        // Exercise the exact metadata policy used before any source bytes or destination writes.
        var error = Assert.ThrowsException<IOException>(() => VerifiedTreeCopy.ValidateCopyAttributes(attributes));
        StringAssert.Contains(error.Message, "EFS-encrypted");
        StringAssert.Contains(error.Message, "encrypted originals were retained");
        VerifiedTreeCopy.ValidateCopyAttributes(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }

    [TestMethod]
    public async Task RootAndNestedShellCustomizationRetainsIniBytesRelativeIconsAndActivationAttributes()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var nested = Path.Combine(source, "custom folder");
        const string ini = "; user metadata\r\n[.ShellClassInfo]\r\nIconResource=folder.ico,0\r\nInfoTip=Keep this text\r\n";
        try
        {
            Directory.CreateDirectory(nested);
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            File.SetAttributes(nested, File.GetAttributes(nested) | FileAttributes.System);
            var metadata = Path.Combine(nested, "desktop.ini");
            await File.WriteAllTextAsync(metadata, ini, System.Text.Encoding.Unicode);
            File.SetAttributes(metadata, FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
            await File.WriteAllBytesAsync(Path.Combine(nested, "folder.ico"), new byte[] { 0, 0, 1, 0 });
            var expected = await File.ReadAllBytesAsync(metadata);
            await VerifiedTreeCopy.CopyAsync(source, destination);
            await VerifiedTreeCopy.CopyAsync(source, destination);
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(Path.Combine(destination, "custom folder", "desktop.ini")));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 0 }, await File.ReadAllBytesAsync(Path.Combine(destination, "custom folder", "folder.ico")));
            Assert.IsTrue((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0);
            Assert.IsTrue((File.GetAttributes(Path.Combine(destination, "custom folder")) & FileAttributes.System) != 0);
            Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System,
                File.GetAttributes(Path.Combine(destination, "custom folder", "desktop.ini")) &
                (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            var before = VerifiedTreeCopy.GetFingerprint(source);
            File.SetAttributes(nested, File.GetAttributes(nested) & ~FileAttributes.System);
            Assert.AreNotEqual(before, VerifiedTreeCopy.GetFingerprint(source), "A changed Shell customization must be visible to the final copy validation.");
        }
        finally
        {
            foreach (var path in new[] { Path.Combine(source, "custom folder", "desktop.ini"), Path.Combine(destination, "custom folder", "desktop.ini") })
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            DeleteGeneratedRoot(root);
        }
    }

    [DataTestMethod]
    [DataRow("My Music")]
    [DataRow("My Pictures")]
    [DataRow("My Videos")]
    [DataRow("Ma musique")]
    public async Task ProtectedCompatibilityJunctionsAreSkippedWithoutReadingOrChangingTheirTargets(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Documents"); var destination = Path.Combine(root, "backup");
        var outside = Path.Combine(root, "outside"); var link = Path.Combine(source, name);
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "empty")); Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "actual document");
            await File.WriteAllTextAsync(Path.Combine(outside, "private.txt"), "separate folder data");
            var targetAttributes = File.GetAttributes(outside);
            var targetSecurity = ReadSecurityDescriptor(outside);
            await CreateJunctionAsync(link, outside);
            MakeCompatibilityJunction(link);
            Assert.ThrowsException<UnauthorizedAccessException>(() => Directory.GetFiles(link));
            var before = VerifiedTreeCopy.GetFingerprint(source);

            await VerifiedTreeCopy.CopyAsync(source, destination);
            await VerifiedTreeCopy.CopyAsync(source, destination); // A retry sees the same skipped metadata.

            Assert.AreEqual(before, VerifiedTreeCopy.GetFingerprint(source));
            Assert.AreEqual("actual document", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(destination, "empty")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, name)), "The compatibility link itself must not be copied.");
            CollectionAssert.AreEquivalent(new[] { "report.txt" }, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
            Assert.AreEqual("separate folder data", await File.ReadAllTextAsync(Path.Combine(outside, "private.txt")));
            Assert.AreEqual(targetAttributes, File.GetAttributes(outside));
            CollectionAssert.AreEqual(targetSecurity, ReadSecurityDescriptor(outside), "Classification must inspect the junction ACL, not modify or inspect its target through the link.");
            Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
        }
        finally { RemoveJunction(link); DeleteGeneratedRoot(root); }
    }

    [DataTestMethod]
    [DataRow("root")]
    [DataRow("ancestor")]
    [DataRow("ordinary")]
    [DataRow("hidden")]
    [DataRow("system")]
    [DataRow("flags-without-deny")]
    [DataRow("deny-without-flags")]
    public async Task SourceLinksWithoutTheCompleteCompatibilitySignatureRemainRejectedBeforeAnyWrite(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside"); var destination = Path.Combine(root, "backup");
        var link = kind is "root" or "ancestor" ? Path.Combine(root, "source") : Path.Combine(root, "source", "My Music");
        var source = kind == "ancestor" ? Path.Combine(link, "child") : Path.Combine(root, "source");
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "child"));
            if (kind is not ("root" or "ancestor"))
            {
                Directory.CreateDirectory(source);
                await File.WriteAllTextAsync(Path.Combine(source, "first.txt"), "document remains");
            }
            await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "outside remains");
            await CreateJunctionAsync(link, outside);
            if (kind is "root" or "ancestor") MakeCompatibilityJunction(link);
            if (kind == "hidden") File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.Hidden);
            if (kind == "system") File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.System);
            if (kind == "flags-without-deny") File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.Hidden | FileAttributes.System);
            if (kind == "deny-without-flags") DenyJunctionEnumeration(link);

            await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, destination));
            Assert.ThrowsException<IOException>(() => VerifiedTreeCopy.GetFingerprint(source));
            Assert.IsFalse(Directory.Exists(destination), "Unsafe source links must fail preflight, not after copying an earlier document.");
            Assert.AreEqual("outside remains", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
            if (kind is not ("root" or "ancestor")) Assert.AreEqual("document remains", await File.ReadAllTextAsync(Path.Combine(source, "first.txt")));
        }
        finally { RemoveJunction(link); DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task HiddenSystemRealDirectoriesAreCopiedAndNotConfusedWithCompatibilityLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var hidden = Path.Combine(source, "hidden documents");
        try
        {
            Directory.CreateDirectory(hidden);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden | FileAttributes.System);
            await File.WriteAllTextAsync(Path.Combine(hidden, "keep.txt"), "hidden actual document");
            await VerifiedTreeCopy.CopyAsync(source, destination);
            Assert.AreEqual("hidden actual document", await File.ReadAllTextAsync(Path.Combine(destination, "hidden documents", "keep.txt")));
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task SkippedCompatibilityJunctionTargetChangesInvalidateTheSourceFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Documents"); var link = Path.Combine(source, "My Music");
        try
        {
            Directory.CreateDirectory(source);
            var firstTarget = Path.Combine(root, "first"); var secondTarget = Path.Combine(root, "second");
            Directory.CreateDirectory(firstTarget); Directory.CreateDirectory(secondTarget);
            await CreateJunctionAsync(link, firstTarget); MakeCompatibilityJunction(link);
            var first = VerifiedTreeCopy.GetFingerprint(source);
            RemoveJunction(link);
            await CreateJunctionAsync(link, secondTarget); MakeCompatibilityJunction(link);
            Assert.AreNotEqual(first, VerifiedTreeCopy.GetFingerprint(source), "Skipping link data must not hide a changed target from final source validation.");
            File.SetAttributes(link, File.GetAttributes(link) & ~FileAttributes.System);
            Assert.ThrowsException<IOException>(() => VerifiedTreeCopy.GetFingerprint(source));
        }
        finally { RemoveJunction(link); DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task DestinationCompatibilityJunctionIsStillRejectedWhenAnActualSourceDirectoryWouldWriteThroughIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "backup");
        var outside = Path.Combine(root, "outside"); var link = Path.Combine(destination, "My Music");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "My Music")); Directory.CreateDirectory(destination); Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(source, "first.txt"), "first remains");
            await File.WriteAllTextAsync(Path.Combine(source, "My Music", "music.txt"), "actual source data");
            await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "outside remains");
            await CreateJunctionAsync(link, outside); MakeCompatibilityJunction(link);
            await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, destination));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "first.txt")));
            Assert.AreEqual("outside remains", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(outside, "music.txt")));
        }
        finally { RemoveJunction(link); DeleteGeneratedRoot(root); }
    }

    [DataTestMethod]
    [DataRow("root")]
    [DataRow("ancestor")]
    [DataRow("nested")]
    public async Task DestinationJunctionsAreRejectedBeforeAnyWrite(string location)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var outside = Path.Combine(root, "outside");
        var link = location == "nested" ? Path.Combine(root, "destination", "nested") : Path.Combine(root, "destination");
        var destination = location == "ancestor" ? Path.Combine(link, "child") : Path.Combine(root, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(source, "first.txt"), "original first file");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "keep.txt"), "original nested file");
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "unrelated outside contents");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            await CreateJunctionAsync(link, outside);
            await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, destination));
            Assert.AreEqual("original first file", await File.ReadAllTextAsync(Path.Combine(source, "first.txt")));
            Assert.AreEqual("original nested file", await File.ReadAllTextAsync(Path.Combine(source, "nested", "keep.txt")));
            Assert.AreEqual("unrelated outside contents", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
            CollectionAssert.AreEquivalent(new[] { "keep.txt" }, Directory.GetFiles(outside).Select(Path.GetFileName).ToArray());
            Assert.IsFalse(Directory.Exists(Path.Combine(outside, "nested")));
            Assert.IsFalse(Directory.Exists(Path.Combine(outside, "child")));
            if (location == "nested") Assert.IsFalse(File.Exists(Path.Combine(destination, "first.txt")), "Preflight must reject the nested link before copying any earlier file.");
        }
        finally
        {
            // Remove the junction itself before recursive cleanup; never follow it to its target.
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link, recursive: false);
            DeleteGeneratedRoot(root);
        }
    }

    [TestMethod]
    public async Task CompleteCopyRetainsOriginalsEmptyFoldersAndConflictingDestinationData()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "empty")); Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "current local data");
            await File.WriteAllTextAsync(Path.Combine(destination, "report.txt"), "older cloud data");
            await VerifiedTreeCopy.CopyAsync(source, destination);
            Assert.AreEqual("current local data", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            Assert.AreEqual("current local data", await File.ReadAllTextAsync(Path.Combine(source, "report.txt")));
            var conflict = Directory.GetFiles(destination, "report (backup conflict *).txt").Single();
            Assert.AreEqual("older cloud data", await File.ReadAllTextAsync(conflict));
            Assert.IsTrue(Directory.Exists(Path.Combine(destination, "empty")));
            await VerifiedTreeCopy.CopyAsync(source, destination);
            Assert.AreEqual(1, Directory.GetFiles(destination, "report (backup conflict *).txt").Length);
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task NestedDestinationIsRejectedBeforeCopying()
    {
        var source = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try { await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyAsync(source, Path.Combine(source, "nested"))); }
        finally { DeleteGeneratedRoot(source); }
    }

    [TestMethod]
    public async Task CancellationDoesNotRedirectOrOverwriteData()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => VerifiedTreeCopy.CopyAsync(root, root + "-copy", cancellation.Token));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(root, "keep.txt")));
            Assert.IsFalse(Directory.Exists(root + "-copy"));
        }
        finally { DeleteGeneratedRoot(root); }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null");
        using var process = Process.Start(start) ?? throw new IOException("The native junction test could not start PowerShell.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.AreEqual(0, process.ExitCode, await error);
        Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
    }

    private static void MakeCompatibilityJunction(string path)
    {
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.System);
        DenyJunctionEnumeration(path);
    }

    private static void DenyJunctionEnumeration(string path)
    {
        // Use OPEN_REPARSE_POINT so the deny ACE belongs to the fixture junction itself.
        using var handle = OpenMetadata(path, 0x00060080);
        var descriptor = new RawSecurityDescriptor(ReadSecurityDescriptor(handle), 0);
        descriptor.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 1,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), false, null));
        var data = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(data, 0);
        if (!SetKernelObjectSecurity(handle, 4, data)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static byte[] ReadSecurityDescriptor(string path)
    {
        using var handle = OpenMetadata(path, 0x00020080);
        return ReadSecurityDescriptor(handle);
    }

    private static byte[] ReadSecurityDescriptor(SafeFileHandle handle)
    {
        Assert.IsFalse(GetKernelObjectSecurity(handle, 4, null, 0, out var length));
        Assert.AreEqual(122, Marshal.GetLastWin32Error());
        var result = new byte[length];
        if (!GetKernelObjectSecurity(handle, 4, result, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    private static SafeFileHandle OpenMetadata(string path, uint access)
    {
        var handle = CreateFileW(path, access, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }

    private static void RemoveJunction(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null) Directory.Delete(path, false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);

    private static void DeleteGeneratedRoot(string root)
    {
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests"));
        if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new IOException("Refusing cleanup outside a generated folder-copy test root.");
        if (Directory.Exists(resolved))
        {
            foreach (var directory in Directory.EnumerateDirectories(resolved, "*", SearchOption.AllDirectories))
                File.SetAttributes(directory, FileAttributes.Directory);
            File.SetAttributes(resolved, FileAttributes.Directory);
            Directory.Delete(resolved, true);
        }
    }
}
