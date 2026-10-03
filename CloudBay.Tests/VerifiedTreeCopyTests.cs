using CloudBay.Core.Sync;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class VerifiedTreeCopyTests
{
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

    private static void DeleteGeneratedRoot(string root)
    {
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudBay.Copy.Tests"));
        if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new IOException("Refusing cleanup outside a generated folder-copy test root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
