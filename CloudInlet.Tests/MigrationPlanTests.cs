using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class MigrationPlanTests
{
    private static MigrationItem Item(string id, params string[] dependencies) => new(id, MigrationArea.ApplicationState,
        "Move " + id, "C:/old/" + id, "C:/new/" + id, "Preserve the owned " + id + " data.", Dependencies: dependencies);

    [TestMethod]
    public void ReviewStartsWithNothingApprovedAndResetClearsEveryChoice()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([Item("state"), Item("root", "state")]));
        Assert.AreEqual(0, selection.SelectedIds.Count);
        Assert.IsFalse(selection.CanContinue);
        selection.Select("root");
        Assert.IsTrue(selection.CanContinue);
        selection.DeselectAll();
        Assert.AreEqual(0, selection.SelectedIds.Count);
        Assert.IsFalse(selection.CanContinue);
    }

    [TestMethod]
    public void SelectingDependentIncludesCompleteSharedDependencyChain()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([
            Item("state"), Item("registry", "state"), Item("root", "state"), Item("cloud", "registry", "root"), Item("other")]));
        selection.Select("cloud");
        Assert.IsTrue(selection.SelectedIds.SetEquals(["state", "registry", "root", "cloud"]));
        selection.Select("cloud");
        Assert.AreEqual(4, selection.SelectedIds.Count);
    }

    [TestMethod]
    public void DeselectingDependencyRemovesAllDependentsButRetainsIndependentChoices()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([
            Item("state"), Item("registry", "state"), Item("root", "state"), Item("cloud", "registry", "root"), Item("other")]));
        selection.SelectAll();
        selection.Deselect("registry");
        Assert.IsTrue(selection.SelectedIds.SetEquals(["state", "root", "other"]));
        selection.Deselect("state");
        Assert.IsTrue(selection.SelectedIds.SetEquals(["other"]));
    }

    [TestMethod]
    public void DeselectingLeafDoesNotSilentlyUndoItsRequiredChoices()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([Item("state"), Item("root", "state")]));
        selection.Select("root");
        selection.Deselect("root");
        Assert.IsTrue(selection.SelectedIds.SetEquals(["state"]));
    }

    [TestMethod]
    public void BlockedDependencyRejectsWholeSelectionWithoutPartiallyChangingIt()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([
            Item("state"), Item("registry") with { BlockedReason = "The destination is already owned." },
            Item("root", "state", "registry"), Item("other")]));
        selection.Select("other");
        Assert.ThrowsException<InvalidOperationException>(() => selection.Select("root"));
        Assert.ThrowsException<InvalidOperationException>(() => selection.Select("registry"));
        Assert.IsTrue(selection.SelectedIds.SetEquals(["other"]));
    }

    [TestMethod]
    public void SelectAllSkipsBlockedBranchesAndRetainsIndependentEligibleItems()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([
            Item("state") with { BlockedReason = "Waiting for transfer recovery." }, Item("registry", "state"),
            Item("root", "registry"), Item("other"), Item("other-child", "other")]));
        selection.SelectAll();
        Assert.IsTrue(selection.SelectedIds.SetEquals(["other", "other-child"]));
        selection.DeselectAll();
        selection.SelectAll();
        Assert.IsTrue(selection.SelectedIds.SetEquals(["other", "other-child"]));
    }

    [TestMethod]
    public void EmptyOrEntirelyBlockedReviewCannotContinue()
    {
        foreach (var items in new[] { Array.Empty<MigrationItem>(), [Item("blocked") with { BlockedReason = "Unavailable." }] })
        {
            var selection = new MigrationSelection(MigrationPlan.Create(items));
            selection.SelectAll();
            Assert.IsFalse(selection.CanContinue);
        }
    }

    [TestMethod]
    public void FingerprintDoesNotDependOnItemOrDependencyPresentationOrder()
    {
        var first = MigrationPlan.Create([Item("state"), Item("registry"), Item("root", "state", "registry")]);
        var second = MigrationPlan.Create([Item("root", "registry", "state"), Item("registry"), Item("state")]);
        Assert.AreEqual(first.Fingerprint, second.Fingerprint);
        Assert.AreEqual(64, first.Fingerprint.Length);
        Assert.IsTrue(first.Fingerprint.All(Uri.IsHexDigit));
        Assert.AreEqual(MigrationPlan.Create([Item("state") with { Dependencies = null }]).Fingerprint,
            MigrationPlan.Create([Item("state")]).Fingerprint);
    }

    [TestMethod]
    public void EveryMaterialReviewFieldChangesItsFingerprint()
    {
        var item = Item("state");
        var original = MigrationPlan.Create([item, Item("registry")]).Fingerprint;
        var variants = new[] {
            item with { Id = "new-id" }, item with { Area = MigrationArea.Installation },
            item with { Title = "Different title" }, item with { CurrentLocation = "C:/different-old" },
            item with { ProposedLocation = "C:/different-new" }, item with { Description = "Different scope." },
            item with { BlockedReason = "Needs review." }, item with { Dependencies = ["registry"] }
        };
        foreach (var changed in variants)
            Assert.AreNotEqual(original, MigrationPlan.Create([changed, Item("registry")]).Fingerprint);
    }

    [TestMethod]
    public void FingerprintKeepsUnambiguousFieldBoundaries()
    {
        var first = Item("state") with { CurrentLocation = "ab", ProposedLocation = "c" };
        var second = first with { CurrentLocation = "a", ProposedLocation = "bc" };
        Assert.AreNotEqual(MigrationPlan.Create([first]).Fingerprint, MigrationPlan.Create([second]).Fingerprint);
    }

    [TestMethod]
    public void DuplicateUnknownAndCyclicDependenciesAreRejected()
    {
        MigrationItem[][] invalid = [
            [Item("state"), Item("state")], [Item("state", "missing")], [Item("state", "state")],
            [Item("state", "registry"), Item("registry", "root"), Item("root", "state")],
            [Item("state"), Item("root", "state", "state")]
        ];
        foreach (var items in invalid) Assert.ThrowsException<ArgumentException>(() => MigrationPlan.Create(items));
    }

    [TestMethod]
    public void MissingFieldsAndUnknownAreasAreRejected()
    {
        var item = Item("state");
        var invalid = new[] {
            item with { Id = " " }, item with { Id = " state" }, item with { Id = "state\n" },
            item with { Title = "" }, item with { CurrentLocation = "\t" }, item with { ProposedLocation = null! },
            item with { Description = "" }, item with { BlockedReason = " " },
            item with { Dependencies = [null!] }, item with { Area = (MigrationArea)999 }
        };
        foreach (var value in invalid) Assert.ThrowsException<ArgumentException>(() => MigrationPlan.Create([value]));
        Assert.ThrowsException<ArgumentNullException>(() => MigrationPlan.Create(null!));
        Assert.ThrowsException<ArgumentNullException>(() => MigrationPlan.Create([null!]));
    }

    [TestMethod]
    public void UnknownSelectionCannotAffectExistingReview()
    {
        var selection = new MigrationSelection(MigrationPlan.Create([Item("state")]));
        selection.Select("state");
        Assert.ThrowsException<ArgumentException>(() => selection.Select("missing"));
        Assert.ThrowsException<ArgumentException>(() => selection.Deselect("missing"));
        Assert.IsTrue(selection.SelectedIds.SetEquals(["state"]));
    }

    [TestMethod]
    public void MutableInputCannotChangeAnExistingPlanOrSelection()
    {
        var dependencies = new List<string> { "state" };
        var items = new List<MigrationItem> { Item("state"), Item("root") with { Dependencies = dependencies } };
        var plan = MigrationPlan.Create(items);
        var selection = new MigrationSelection(plan);
        dependencies.Clear();
        items.Clear();
        selection.Select("root");
        Assert.AreEqual(2, plan.Items.Count);
        Assert.IsTrue(selection.SelectedIds.SetEquals(["state", "root"]));
        Assert.AreEqual(plan.Fingerprint, MigrationPlan.Create(plan.Items).Fingerprint);
        var previousSelection = selection.SelectedIds;
        selection.DeselectAll();
        Assert.IsTrue(previousSelection.SetEquals(["state", "root"]));
    }

    [TestMethod]
    public void ModifiedOrForgedPlanCannotReuseEarlierReviewFingerprint()
    {
        var plan = MigrationPlan.Create([Item("state")]);
        Assert.ThrowsException<ArgumentException>(() => new MigrationSelection(plan with { Fingerprint = new string('0', 64) }));
        Assert.ThrowsException<ArgumentException>(() => new MigrationSelection(plan with {
            Items = [Item("state") with { ProposedLocation = "C:/changed-after-review" }] }));
    }
}
