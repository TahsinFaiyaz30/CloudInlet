using System.Diagnostics;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class PathRulesTests
{
    [DataTestMethod]
    [DataRow("../outside.txt")]
    [DataRow("folder/../../outside.txt")]
    [DataRow("C:/outside.txt")]
    [DataRow("folder/stream:secret")]
    [DataRow("CON.txt")]
    [DataRow("x/LPT1.log")]
    [DataRow("COM¹.txt")]
    [DataRow("folder/com².log")]
    [DataRow("COM³")]
    [DataRow("folder/LPT¹.txt")]
    [DataRow("lpt²")]
    [DataRow("LPT³.tar.gz")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    [DataRow("empty//name")]
    [DataRow("back\\slash.txt")]
    public void RejectsUnsafeCloudNames(string value) => Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateRelative(value));
    [TestMethod]
    public void ValidNamesAndExclusionsPreserveExpectedSemantics()
    {
        Assert.AreEqual("Photos/2026/photo.jpg", PathRules.FromKey("CloudBay/Photos/2026/photo.jpg", "CloudBay/"));
        Assert.IsTrue(PathRules.IsExcluded("project/node_modules/index.js", ["node_modules"]));
        Assert.IsTrue(PathRules.IsExcluded("Documents/~$draft.docx", ["~$*"]));
        Assert.IsFalse(PathRules.IsExcluded("Documents/final.docx", ["~$*"]));
        Assert.IsTrue(PathRules.IsExcluded(".cloudbay/transfers/partial", []));
    }

    [TestMethod]
    public void ConflictNamesRetainUniqueSuffixExtensionAndValidUnicodeWithinWindowsLimit()
    {
        const string suffix = " (conflict 20261004-123456-abcdef)";
        var result = PathRules.ConflictFileName(new string('x', 250) + ".txt", suffix);
        Assert.IsTrue(result.Length <= 255);
        StringAssert.EndsWith(result, suffix + ".txt");
        PathRules.ValidateRelative(result);
        var unicode = PathRules.ConflictFileName(string.Concat(Enumerable.Repeat("🙂", 125)) + ".png", suffix);
        Assert.IsTrue(unicode.Length <= 255);
        StringAssert.EndsWith(unicode, suffix + ".png");
        for (var index = 0; index < unicode.Length; index++)
            if (char.IsHighSurrogate(unicode[index])) Assert.IsTrue(index + 1 < unicode.Length && char.IsLowSurrogate(unicode[++index]));
        Assert.AreNotEqual(result, PathRules.ConflictFileName(new string('x', 250) + ".txt", suffix.Replace("abcdef", "uvwxyz")));
    }

    [TestMethod]
    public void ExtendedNativePathsPreserveDriveAndUncPathsAndRejectDeviceNamespaces()
    {
        Assert.AreEqual(@"\\?\C:\folder\file.txt", WindowsFilePaths.ToExtendedPath(@"C:\folder\file.txt"));
        Assert.AreEqual(@"\\?\UNC\server\share\folder\file.txt", WindowsFilePaths.ToExtendedPath(@"\\server\share\folder\file.txt"));
        Assert.AreEqual(@"\\?\C:\folder\file.txt", WindowsFilePaths.ToExtendedPath(@"\\?\C:\folder\file.txt"));
        Assert.ThrowsException<ArgumentException>(() => WindowsFilePaths.ToExtendedPath(@"relative\file.txt"));
        Assert.ThrowsException<ArgumentException>(() => WindowsFilePaths.ToExtendedPath(@"\\.\PhysicalDrive0"));
        Assert.ThrowsException<ArgumentException>(() => WindowsFilePaths.ToExtendedPath(@"\\?\GLOBALROOT\Device\HarddiskVolume1\file.txt"));
    }

    [TestMethod]
    public void PickerSelectionsKeepRootScopeAndSystemFolderPrefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", Guid.NewGuid().ToString("N"));
        var main = Path.Combine(parent, "Main");
        var projects = Path.Combine(parent, "Projects");
        var archive = Path.Combine(parent, "Archive");
        var settings = new AppSettings
        {
            RootPath = main,
            CustomBackups = [new("Projects", projects, "CloudBay/.cloudbay-backups/Projects/"),
                new("Archive", archive, "CloudBay/.cloudbay-backups/Archive/")]
        };
        var selected = PathRules.CreateSelectedExclusion(settings, Path.Combine(projects, "Shared", "notes.txt"), false);
        Assert.AreEqual(projects, selected.RootPath);
        Assert.AreEqual("Shared/notes.txt", selected.RelativePath);
        settings = settings with { SelectedExclusions = [selected] };
        Assert.IsFalse(PathRules.IsExcluded("Shared/notes.txt", settings));
        Assert.IsTrue(PathRules.IsExcluded("shared/NOTES.txt", settings with { RootPath = projects }));
        Assert.IsFalse(PathRules.IsExcluded("Shared/notes.txt", settings with { RootPath = archive }));

        var desktop = PathRules.CreateSelectedExclusion(settings, Path.Combine(main, "Desktop", "Private"), true);
        Assert.AreEqual(main, desktop.RootPath);
        Assert.AreEqual("Desktop/Private", desktop.RelativePath);
        Assert.IsTrue(desktop.IsFolder);
        PathRules.ValidateSettings(settings with { CustomBackups = [] });
        Assert.IsFalse(PathRules.IsExcluded("Shared/notes.txt", settings with { RootPath = Path.Combine(parent, "NewProjects") }),
            "An inactive root-scoped rule must not migrate to an unrelated backup.");
    }

    [TestMethod]
    public void LiteralSelectionsDistinguishFilesFromFoldersAndRespectComponentBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { RootPath = root, SelectedExclusions = [new(root, "Desktop/Private", true), new(root, "Desktop/item.txt", false)] };
        Assert.IsTrue(PathRules.IsExcluded("Desktop/Private", settings, isDirectory: true));
        Assert.IsTrue(PathRules.IsExcluded("Desktop/Private/nested/file.txt", settings));
        Assert.IsFalse(PathRules.IsExcluded("Desktop/Privateish/file.txt", settings));
        Assert.IsFalse(PathRules.IsExcluded("Documents/Private/file.txt", settings));
        Assert.IsFalse(PathRules.IsExcluded("Desktop/Private", settings), "A file replacing an excluded folder is a new, distinct item.");
        Assert.IsTrue(PathRules.IsExcluded("desktop/ITEM.txt", settings));
        Assert.IsFalse(PathRules.IsExcluded("Desktop/item.txt", settings, isDirectory: true));
        Assert.IsFalse(PathRules.IsExcluded("Desktop/item.txt/child.txt", settings),
            "Replacing a selected file with a directory must not exclude that directory's contents.");
    }

    [DataTestMethod]
    [DataRow("../outside")]
    [DataRow("folder/../../outside")]
    [DataRow("folder/stream:secret")]
    [DataRow("folder/*")]
    [DataRow("folder/?.txt")]
    [DataRow("folder//name")]
    [DataRow("back\\slash")]
    [DataRow("")]
    public void SavedLiteralSelectionsRejectTraversalWildcardsAndUnsafePaths(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(new()
        { RootPath = root, SelectedExclusions = [new(root, relative, false)] }));
    }

    [TestMethod]
    public void PickerSelectionsRejectOutsidePathsRootSelectionAndTraversalBeforeCanonicalization()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { RootPath = root };
        foreach (var path in new[] { "relative/file.txt", root, root + "ish\\file.txt",
            Path.Combine(root, "..", Path.GetFileName(root), "file.txt"), Path.Combine(root, "stream:secret") })
            Assert.ThrowsException<InvalidDataException>(() => PathRules.CreateSelectedExclusion(settings, path, false), path);
    }

    [DataTestMethod]
    [DataRow("*.tmp", ExclusionTarget.Files, "build.tmp", false, true)]
    [DataRow("*.tmp", ExclusionTarget.Files, "build.tmp", true, false)]
    [DataRow("*.tmp", ExclusionTarget.Files, "build.tmp/keep.txt", false, false)]
    [DataRow("*.tmp", ExclusionTarget.Files, "nested/FILE.TMP", false, true)]
    [DataRow("cache", ExclusionTarget.Folders, "project/cache", true, true)]
    [DataRow("cache", ExclusionTarget.Folders, "project/cache/keep.txt", false, true)]
    [DataRow("cache", ExclusionTarget.Folders, "project/cache", false, false)]
    [DataRow("cache", ExclusionTarget.Folders, "project/cacheish/keep.txt", false, false)]
    [DataRow("cache", ExclusionTarget.All, "project/cache", false, true)]
    [DataRow("cache", ExclusionTarget.All, "project/cache/keep.txt", false, true)]
    [DataRow("Projects/*/bin", ExclusionTarget.Folders, "Projects/tool/bin/output.dll", false, true)]
    [DataRow("Projects/*/bin", ExclusionTarget.Folders, "Projects/tool/extra/bin/output.dll", false, false)]
    [DataRow("Projects/?.tmp", ExclusionTarget.Files, "Projects/a.tmp", false, true)]
    [DataRow("Projects/?.tmp", ExclusionTarget.Files, "Projects/ab.tmp", false, false)]
    [DataRow("Projects/*.tmp", ExclusionTarget.Files, "Projects/tool/a.tmp", false, false)]
    [DataRow("**/*.tmp", ExclusionTarget.Files, "a.tmp", false, true)]
    [DataRow("**/*.tmp", ExclusionTarget.Files, "one/two/a.tmp", false, true)]
    [DataRow("Projects/**/bin", ExclusionTarget.Folders, "Projects/bin/out.dll", false, true)]
    [DataRow("Projects/**/bin", ExclusionTarget.Folders, "Projects/a/b/bin/out.dll", false, true)]
    [DataRow("Projects/**/bin", ExclusionTarget.Folders, "Other/Projects/bin/out.dll", false, false)]
    [DataRow("**/cache/**", ExclusionTarget.Folders, "cache/item.txt", false, true)]
    [DataRow("**/cache/**", ExclusionTarget.Folders, "one/cache/two/item.txt", false, true)]
    [DataRow("**/cache/**", ExclusionTarget.Folders, "cache", false, false)]
    [DataRow("a/**/**/b?.tmp", ExclusionTarget.Files, "a/b1.tmp", false, true)]
    [DataRow("a/**/**/b?.tmp", ExclusionTarget.Files, "a/one/two/b1.tmp", false, true)]
    public void GuidedRulesRespectTargetAncestorAndWildcardSemantics(string pattern, ExclusionTarget target, string path, bool isDirectory, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        var settings = new AppSettings { RootPath = root, GuidedExclusions = [new(pattern, target)] };
        PathRules.ValidateSettings(settings);
        Assert.AreEqual(expected, PathRules.IsExcluded(path, settings, isDirectory));
    }

    [TestMethod]
    public void GuidedScopesRemainInertInOtherRootsAndLegacyComponentPatternsRemainCompatible()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", Guid.NewGuid().ToString("N"));
        var main = Path.Combine(parent, "Main");
        var other = Path.Combine(parent, "Other");
        var settings = new AppSettings { RootPath = main, GuidedExclusions = [new("*.tmp", ExclusionTarget.Files, main), new("cache", ExclusionTarget.Folders)] };
        Assert.IsTrue(PathRules.IsExcluded("notes.tmp", settings));
        Assert.IsFalse(PathRules.IsExcluded("notes.tmp", settings with { RootPath = other }));
        Assert.IsTrue(PathRules.IsExcluded("cache/notes.txt", settings with { RootPath = other }));
        Assert.IsTrue(PathRules.IsExcluded("~$temp/notes.txt", settings), "Legacy Office-temporary component patterns remain compatible.");
    }

    [TestMethod]
    public void FolderScopesApplyOnlyInsideThatExactFolderAndDisabledRulesRemainInert()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        var settings = new AppSettings
        {
            RootPath = root, Exclusions = [],
            SelectedExclusions = [new(root, "hidden", true, Enabled: false)],
            GuidedExclusions = [new("*.tmp", ExclusionTarget.Files, root, "Projects/cache"),
                new("cache", ExclusionTarget.Folders, root, "Projects/cache"),
                new("*.secret", ExclusionTarget.Files, Enabled: false)]
        };
        PathRules.ValidateSettings(settings);
        Assert.IsTrue(PathRules.IsExcluded("projects/CACHE/a.tmp", settings));
        Assert.IsTrue(PathRules.IsExcluded("Projects/cache/one/two/a.tmp", settings));
        Assert.IsTrue(PathRules.IsExcluded("Projects/cache/nested/cache/keep.txt", settings));
        Assert.IsFalse(PathRules.IsExcluded("Projects/cache", settings, isDirectory: true), "The scope folder itself is not a matching child.");
        Assert.IsFalse(PathRules.IsExcluded("Projects/cacheish/a.tmp", settings));
        Assert.IsFalse(PathRules.IsExcluded("Projects/other/a.tmp", settings));
        Assert.IsFalse(PathRules.IsExcluded("hidden/file.txt", settings));
        Assert.IsFalse(PathRules.IsExcluded("Projects/cache/file.secret", settings));
        Assert.IsFalse(PathRules.IsExcluded("Projects/cache/a.tmp", settings with { RootPath = root + "Other" }));
    }

    [TestMethod]
    public void LegacyTogglesKeepOriginalSlashWildcardSemanticsAndBuiltInProtection()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        var settings = new AppSettings { RootPath = root, Exclusions = ["Projects/*.tmp", "~$*"], DisabledLegacyExclusions = ["PROJECTS/*.TMP", "~$*"] };
        PathRules.ValidateSettings(settings);
        Assert.IsFalse(PathRules.IsExcluded("Projects/one/two/item.tmp", settings));
        Assert.IsFalse(PathRules.IsExcluded("~$document.docx", settings));
        Assert.IsTrue(PathRules.IsExcluded(".cloudbay/transfers/file.tmp", settings));
        Assert.IsTrue(PathRules.IsExcluded(".cloudbay-backups/Projects/file.tmp", settings));
        var enabled = settings with { DisabledLegacyExclusions = [] };
        Assert.IsTrue(PathRules.IsExcluded("Projects/one/two/item.tmp", enabled), "Original legacy '*' crosses separators and is restored unchanged.");
        Assert.IsFalse(PathRules.IsExcluded("Projects/one/two/item.tmp", enabled with { Exclusions = [], GuidedExclusions = [new("Projects/*.tmp", ExclusionTarget.All)] }),
            "Guided '*' intentionally matches one component; toggling legacy must never silently convert it.");
        Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(settings with { DisabledLegacyExclusions = ["missing"] }));
    }

    [DataTestMethod]
    [DataRow("../outside")]
    [DataRow("folder//*.tmp")]
    [DataRow("C:/*.tmp")]
    [DataRow("folder\\*.tmp")]
    [DataRow("folder/stream:secret")]
    [DataRow("")]
    [DataRow("a/**b/*.tmp")]
    [DataRow("a/b**/*.tmp")]
    [DataRow("a/***/*.tmp")]
    public void GuidedPatternsRejectUnsafeLiteralComponents(string pattern)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(new()
        { RootPath = root, GuidedExclusions = [new(pattern, ExclusionTarget.All)] }));
    }

    [TestMethod]
    public void ScopedRulesRejectUnknownTargetsAndNonCanonicalAbsoluteRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(new()
        { RootPath = root, GuidedExclusions = [new("*.tmp", (ExclusionTarget)99)] }));
        foreach (var scope in new[] { "relative", root + "\\", Path.Combine(root, "..", "Main"), Path.GetPathRoot(root)! })
        {
            Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(new()
            { RootPath = root, SelectedExclusions = [new(scope, "file.txt", false)] }));
            Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(new()
            { RootPath = root, GuidedExclusions = [new("*.tmp", ExclusionTarget.Files, scope)] }));
        }
    }

    [TestMethod]
    public void GuidedPatternWorkIsBoundedAndDeepWildcardPathsUseIterativeMatching()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", "Main");
        foreach (var pattern in new[] { new string('a', 256), string.Join('/', Enumerable.Repeat("a", 129)),
            string.Join('/', Enumerable.Repeat(new string('a', 200), 21)) })
            Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateSettings(new() { RootPath = root, GuidedExclusions = [new(pattern, ExclusionTarget.All)] }));
        var repeated = string.Join('/', Enumerable.Repeat("**/a", 60)) + "/**/file.txt";
        var path = string.Join('/', Enumerable.Repeat("a", 100)) + "/file.txt";
        var settings = new AppSettings { RootPath = root, Exclusions = [], GuidedExclusions = [new(repeated, ExclusionTarget.Files)] };
        PathRules.ValidateSettings(settings);
        Assert.IsTrue(PathRules.IsExcluded(path, settings));
        Assert.IsFalse(PathRules.IsExcluded(path.Replace("file.txt", "other.txt"), settings));
    }

    [TestMethod]
    public void PickersRejectItemTypeMismatchWithoutReadingFileContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Folder"));
        try
        {
            File.WriteAllText(Path.Combine(root, "File.txt"), "retained");
            var settings = new AppSettings { RootPath = root };
            Assert.ThrowsException<InvalidDataException>(() => PathRules.CreateSelectedExclusion(settings, Path.Combine(root, "Folder"), false));
            Assert.ThrowsException<InvalidDataException>(() => PathRules.CreateSelectedExclusion(settings, Path.Combine(root, "File.txt"), true));
            Assert.AreEqual("retained", File.ReadAllText(Path.Combine(root, "File.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PickersRejectJunctionSelectionsLinkedParentsAndLinkedRootAncestors()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CloudBay.Exclusion.Tests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Main");
        var target = Path.Combine(parent, "External");
        var link = Path.Combine(root, "Link");
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(target, "Root"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(target, "Root", "keep.txt"), "retained external data");
            static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null");
            using var process = Process.Start(start) ?? throw new IOException("The junction test could not start PowerShell.");
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(); await output;
            Assert.AreEqual(0, process.ExitCode, await error);
            var settings = new AppSettings { RootPath = root };
            Assert.ThrowsException<IOException>(() => PathRules.CreateSelectedExclusion(settings, link, true));
            Assert.ThrowsException<IOException>(() => PathRules.CreateSelectedExclusion(settings, Path.Combine(link, "Root", "keep.txt"), false));
            Assert.ThrowsException<IOException>(() => PathRules.CreateSelectedExclusion(settings with { RootPath = Path.Combine(link, "Root") }, Path.Combine(link, "Root", "keep.txt"), false));
            Assert.AreEqual("retained external data", await File.ReadAllTextAsync(Path.Combine(target, "Root", "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }
}
