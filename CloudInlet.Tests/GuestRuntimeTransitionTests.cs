using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class GuestRuntimeTransitionTests
{
    [DataTestMethod]
    [DataRow("valid")]
    [DataRow("changed-inventory")]
    [DataRow("changed-boundary")]
    [DataRow("duplicate")]
    [DataRow("same-runtime")]
    public void RuntimeUpgradeRequiresPinnedPreviousInventoryAndUnchangedBoundary(string scenario)
    {
        var saved = new Dictionary<string, string> { ["CloudInlet.Tests.dll"] = new('A', 64), ["CloudInlet.Core.dll"] = new('B', 64) };
        var current = new Dictionary<string, string>(saved) { ["CloudInlet.Tests.dll"] = new('C', 64) };
        var files = saved.Select(pair => new { path = "tests/" + pair.Key, sha256 = pair.Value }).ToList();
        if (scenario == "duplicate") files.Add(files[0]);
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, files });
        var hash = Convert.ToHexString(SHA256.HashData(inventory));
        if (scenario == "changed-inventory") inventory[^2] ^= 1;
        if (scenario == "changed-boundary") saved["CloudInlet.Tests.dll"] = new('D', 64);
        if (scenario == "same-runtime") current = new(saved);
        if (scenario == "valid") GuestRuntimeTransition.Validate(saved, current, inventory, hash);
        else Assert.ThrowsException<InvalidDataException>(() => GuestRuntimeTransition.Validate(saved, current, inventory, hash));
    }
}
