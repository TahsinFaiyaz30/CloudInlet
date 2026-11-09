using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;
[TestClass]
public sealed class CloudPrefixKeyMapTests
{
    private static readonly CloudPrefixRule Backup = new("CloudBay/.cloudbay-backups/Documents/", "CloudInlet/.cloudinlet-backups/Documents/");
    [TestMethod]
    public void MappingUsesReviewedNestedFolderAndPreservesOrdinaryNames()
    {
        var map = new CloudPrefixKeyMap("CloudBay/", "CloudInlet/", [Backup]);
        Assert.AreEqual("CloudInlet/file.txt", map.Map("CloudBay/file.txt"));
        Assert.AreEqual("CloudInlet/.cloudinlet-backups/Documents/a.txt", map.Map("CloudBay/.cloudbay-backups/Documents/a.txt"));
        Assert.AreEqual("CloudInlet/.cloudbay-backups/Custom/a.txt", map.Map("CloudBay/.cloudbay-backups/Custom/a.txt"));
    }
    [TestMethod]
    public void CollidingOriginalNamesAreRejectedBeforeCopyingHistory()
    {
        var map = new CloudPrefixKeyMap("CloudBay/", "CloudInlet/", [Backup]);
        Assert.ThrowsException<InvalidDataException>(() => map.ValidateDistinctKeys([
            "CloudBay/.cloudbay-backups/Documents/a.txt", "CloudBay/.cloudinlet-backups/Documents/a.txt"]));
        map.ValidateDistinctKeys(["CloudBay/file.txt", "CloudBay/file.txt"]);
    }
    [TestMethod]
    public void RulesAreImmutableAndChangesInvalidateReviewedConsent()
    {
        var rules = new List<CloudPrefixRule> { Backup };
        var item = new MigrationItem("cloud", MigrationArea.CloudPrefix, "Cloud folder", "CloudBay/", "CloudInlet/", "Preserve history", CloudMappings: rules);
        var plan = MigrationPlan.Create([item]); rules.Clear();
        Assert.AreEqual(1, plan.Items.Single().CloudMappings!.Count);
        Assert.AreNotEqual(plan.Fingerprint, MigrationPlan.Create([item]).Fingerprint);
        Assert.AreEqual(plan.Fingerprint, MigrationPlan.Create(plan.Items).Fingerprint);
    }
    [TestMethod]
    public void ExistingReviewsWithoutNestedMappingKeepTheirOriginalFingerprint()
    {
        var item = new MigrationItem("state", MigrationArea.ApplicationState, "Application data", "C:/old", "C:/new", "Preserve data");
        var legacy = new { Format = "CloudInlet.MigrationReview.v1", Items = new[] { new
        { item.Id, item.Area, item.Title, item.CurrentLocation, item.ProposedLocation, item.Description, item.BlockedReason, Dependencies = Array.Empty<string>() } } };
        var expected = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy))).ToLowerInvariant();
        Assert.AreEqual(expected, MigrationPlan.Create([item]).Fingerprint);
    }
    [TestMethod]
    public void RulesCannotEscapeReviewOrDuplicateDestinations()
    {
        Assert.ThrowsException<ArgumentException>(() => new CloudPrefixKeyMap("CloudBay/", "CloudInlet/", [Backup with { DestinationPrefix = "Other/" }]));
        Assert.ThrowsException<ArgumentException>(() => new CloudPrefixKeyMap("CloudBay/", "CloudInlet/", [Backup, Backup]));
        Assert.ThrowsException<ArgumentException>(() => MigrationPlan.Create([new("state", MigrationArea.ApplicationState, "Data", "C:/old", "C:/new", "Preserve", CloudMappings: [Backup])]));
    }
}
