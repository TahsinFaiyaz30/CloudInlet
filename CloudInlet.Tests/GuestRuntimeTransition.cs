using System.Security.Cryptography;
using System.Text.Json;

namespace CloudInlet.Tests;

internal static class GuestRuntimeTransition
{
    // This authenticates a deliberately selected older harness against the
    // unchanged protected boundary. It cannot turn its failed reboot into a pass.
    public static void Validate(IReadOnlyDictionary<string, string> saved, IReadOnlyDictionary<string, string> current,
        byte[] previousInventory, string expectedInventoryHash)
    {
        if (previousInventory.Length is < 1 or > 256 * 1024 || expectedInventoryHash is null ||
            expectedInventoryHash.Length != 64 || !expectedInventoryHash.All(Uri.IsHexDigit) ||
            !Convert.ToHexString(SHA256.HashData(previousInventory)).Equals(expectedInventoryHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The explicitly reviewed previous runtime inventory changed.");
        using var parsed = JsonDocument.Parse(previousInventory);
        if (parsed.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported previous inventory.");
        var files = parsed.RootElement.GetProperty("files").EnumerateArray().ToArray();
        if (files.Length is < 1 or > 2000 || saved.Count != 2 || current.Count != saved.Count ||
            !saved.ContainsKey("CloudInlet.Tests.dll") || !saved.ContainsKey("CloudInlet.Core.dll"))
            throw new InvalidDataException("Unexpected protected runtime inventory.");
        foreach (var binary in saved)
        {
            var matches = files.Where(file => file.GetProperty("path").GetString() == "tests/" + binary.Key).ToArray();
            if (!current.ContainsKey(binary.Key) || matches.Length != 1 ||
                !string.Equals(matches[0].GetProperty("sha256").GetString(), binary.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The protected boundary does not belong to the reviewed previous runtime.");
        }
        if (saved.All(binary => string.Equals(current[binary.Key], binary.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This explicit stage requires a changed runtime; use ordinary recovery for unchanged binaries.");
    }
}
