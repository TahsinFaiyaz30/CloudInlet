using CloudInlet.Core;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ImportSourceDiscoveryTests
{
    private static AppSettings Settings => new() { RootPath = @"C:\Users\Example\CloudInlet" };
    private static ImportSourceCandidate Root(string path, string provider = "Microsoft OneDrive", string? id = null) =>
        new(id ?? path, Path.GetFileName(path), provider, path, "Registered cloud folder");
    private static Func<string, bool> Existing(params string[] paths)
    {
        var existing = new HashSet<string>(paths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return path => existing.Contains(Path.GetFullPath(path));
    }

    [TestMethod]
    public void PicturesListsOnlyPicturesForEachAccount()
    {
        var personal = @"C:\Users\Example\OneDrive";
        var work = @"C:\Users\Example\OneDrive - Work";
        var result = ImportSourceDiscovery.FilterCandidates([Root(personal), Root(work)], Settings, "Pictures",
            Existing(personal, work, Path.Combine(personal, "Pictures"), Path.Combine(work, "Pictures"), Path.Combine(work, "Documents")));
        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEquivalent(new[] { Path.Combine(personal, "Pictures"), Path.Combine(work, "Pictures") },
            result.Select(item => item.Path).ToArray());
        Assert.IsTrue(result.All(item => item.DisplayName.EndsWith(" · Pictures", StringComparison.Ordinal)));
        Assert.IsTrue(result.All(item => item.ProviderName == "Microsoft OneDrive"));
    }

    [TestMethod]
    public void EverySupportedPersonalFolderUsesItsOwnContextOnly()
    {
        var account = @"C:\Users\Example\OneDrive";
        foreach (var folder in KnownFolderBackup.FolderIds.Keys)
        {
            var result = ImportSourceDiscovery.FilterCandidates([Root(account)], Settings, folder.ToLowerInvariant(), _ => true);
            Assert.AreEqual(1, result.Count, folder);
            Assert.AreEqual(Path.Combine(account, folder), result[0].Path, folder);
            Assert.AreEqual(folder + " folder", result[0].Kind, folder);
        }
    }

    [TestMethod]
    public void WholeCloudChoiceReturnsOnlyCanonicalAccountRootsOnce()
    {
        var account = @"C:\Users\Example\OneDrive";
        var hint = Root(account + @"\.") with { Id = account, Kind = "Existing Windows folder", DisplayName = "Fallback" };
        var registered = Root(account.ToUpperInvariant(), id: "OneDrive!S-1-2!Personal") with { DisplayName = "Personal account" };
        var result = ImportSourceDiscovery.FilterCandidates([hint, registered], Settings, directoryExists: _ => true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(account.ToUpperInvariant(), result[0].Path);
        Assert.AreEqual("OneDrive!S-1-2!Personal", result[0].Id);
        Assert.AreEqual("Personal account", result[0].DisplayName);
    }

    [TestMethod]
    public void MissingMatchingFolderDoesNotOfferItsAccountRootOrAnotherFolder()
    {
        var account = @"C:\Users\Example\OneDrive";
        var result = ImportSourceDiscovery.FilterCandidates([Root(account)], Settings, "Pictures",
            Existing(account, Path.Combine(account, "Documents")));
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void AllCloudInletProviderIdentitiesAndTheirFallbackAliasesAreExcluded()
    {
        var generated = @"C:\Users\Example\CloudInlet-NativeIntent-existing";
        var byGuid = @"C:\Users\Example\OwnGuidRoot";
        var byName = @"C:\Users\Example\OwnProviderName";
        var another = @"C:\Users\Example\OneDrive";
        var roots = new[]
        {
            Root(generated, id: "CloudInlet!S-1-2!fixture") with { ProviderName = "Unknown provider" },
            Root(generated) with { Kind = "Existing Windows folder" },
            Root(byGuid, "Unknown provider", "Another!S-1-2!Account") with { ProviderId = new("cb8ba37b-49da-4693-a2be-bf88b98c6225") },
            Root(byName, "CloudInlet"),
            Root(another)
        };
        var result = ImportSourceDiscovery.FilterCandidates(roots, Settings, directoryExists: _ => true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(another, result[0].Path);
        var pictures = ImportSourceDiscovery.FilterCandidates(roots, Settings, "Pictures", _ => true);
        Assert.AreEqual(1, pictures.Count);
        Assert.AreEqual(Path.Combine(another, "Pictures"), pictures[0].Path);
    }

    [TestMethod]
    public void CurrentLocalizedMappingIsPreservedForItsAccountWithoutDuplicateDefaultFolder()
    {
        var personal = @"C:\Users\Example\OneDrive";
        var work = @"C:\Users\Example\OneDrive - Work";
        var mapped = Path.Combine(work, "Personal", "Family photos");
        var result = ImportSourceDiscovery.FilterCandidates([Root(personal), Root(work)], Settings, "Pictures", _ => true, mapped);
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Any(item => item.Path == mapped));
        Assert.IsTrue(result.Any(item => item.Path == Path.Combine(personal, "Pictures")));
        Assert.IsFalse(result.Any(item => item.Path == Path.Combine(work, "Pictures")));
    }

    [TestMethod]
    public void UnrelatedCurrentKnownFolderMappingCannotBePresentedAsCloudSource()
    {
        var account = @"C:\Users\Example\OneDrive";
        var outside = @"C:\Users\Example\Pictures";
        var result = ImportSourceDiscovery.FilterCandidates([Root(account)], Settings, "Pictures", _ => true, outside);
        Assert.AreEqual(Path.Combine(account, "Pictures"), result.Single().Path);
    }

    [TestMethod]
    public void ContextMayUseSiblingOfCustomBackupButWholeAccountCannotOverlapIt()
    {
        var account = @"C:\Users\Example\OneDrive";
        var settings = Settings with { CustomBackups = [new("Documents", Path.Combine(account, "Documents"), "Documents/")] };
        Assert.AreEqual(0, ImportSourceDiscovery.FilterCandidates([Root(account)], settings, directoryExists: _ => true).Count);
        Assert.AreEqual(0, ImportSourceDiscovery.FilterCandidates([Root(account)], settings, "Documents", _ => true).Count);
        Assert.AreEqual(Path.Combine(account, "Pictures"),
            ImportSourceDiscovery.FilterCandidates([Root(account)], settings, "Pictures", _ => true).Single().Path);
    }

    [TestMethod]
    public void OwnActiveSyncRootAndBoundaryNeighborsAreHandledSeparately()
    {
        var root = Settings.RootPath;
        var neighbor = root + "Archive";
        var result = ImportSourceDiscovery.FilterCandidates([Root(root), Root(root + @"\Nested"), Root(neighbor)], Settings,
            directoryExists: _ => true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(neighbor, result[0].Path);
    }

    [TestMethod]
    public void RelativeMalformedAndUnsupportedContextsCannotEscapeTheirAccount()
    {
        var roots = new[] { Root(@"C:\Users\Example\OneDrive"), Root(@"..\Relative"), Root(@"C:\bad\path\" + '\0') };
        Assert.AreEqual(1, ImportSourceDiscovery.FilterCandidates(roots, Settings, directoryExists: _ => true).Count);
        Assert.ThrowsException<ArgumentException>(() => ImportSourceDiscovery.FilterCandidates(roots, Settings, @"..\Documents", _ => true));
        Assert.ThrowsException<ArgumentException>(() => ImportSourceDiscovery.FilterCandidates(roots, Settings, "Unknown", _ => true));
    }
}
