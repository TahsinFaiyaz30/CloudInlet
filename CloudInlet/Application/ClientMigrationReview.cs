using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core.Migration;

namespace CloudInlet.Application;

/// <summary>Durable consent, outside every directory the reviewed operations may move.</summary>
internal sealed class ClientMigrationReview
{
    internal sealed record Consent(int SchemaVersion, string Id, MigrationPlan Plan, string[] SelectedIds,
        bool CloudMaintenanceAcknowledged = false);
    private static readonly byte[] Entropy = "CloudInlet.MigrationConsent.v1"u8.ToArray();
    private readonly string _directory;
    internal string ConsentPath => Path.Combine(_directory, "pending-review.dpapi");
    internal ClientMigrationReview(string directory)
    {
        _directory = Path.GetFullPath(directory);
        EnsureUnlinked(_directory);
        Directory.CreateDirectory(_directory);
    }

    internal string SessionPath(Consent consent) => Path.Combine(_directory, "session-" + consent.Id + ".json");
    internal string ReceiptDirectory(Consent consent) => Path.Combine(_directory, "receipts-" + consent.Id);

    internal Consent? Load()
    {
        EnsureUnlinked(ConsentPath);
        if (!File.Exists(ConsentPath)) return null;
        using var input = new FileStream(ConsentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("The migration consent record has an invalid size.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var plaintext = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var consent = JsonSerializer.Deserialize<Consent>(plaintext) ?? throw new InvalidDataException("The migration review is empty.");
            Validate(consent);
            return consent;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal Consent Create(MigrationPlan plan, IEnumerable<string> selected, bool cloudMaintenanceAcknowledged = false)
    {
        var consent = new Consent(1, Guid.NewGuid().ToString("N"), plan, selected.Order(StringComparer.Ordinal).ToArray(), cloudMaintenanceAcknowledged);
        Validate(consent);
        Write(ConsentPath, JsonSerializer.SerializeToUtf8Bytes(consent), overwrite: false);
        return consent;
    }

    internal void Finish(Consent consent)
    {
        var saved = Load() ?? throw new InvalidDataException("The pending migration review is missing.");
        if (saved.Id != consent.Id || saved.Plan.Fingerprint != consent.Plan.Fingerprint)
            throw new InvalidDataException("The migration review changed while it was running.");
        var destination = Path.Combine(_directory, "finished-" + consent.Id + ".dpapi");
        EnsureUnlinked(destination);
        File.Move(ConsentPath, destination); // Retain the review and journal for diagnostics; never overwrite history.
    }

    internal bool WasOffered(string fingerprint)
    {
        var path = Path.Combine(_directory, "last-offered.txt"); EnsureUnlinked(path);
        return File.Exists(path) && new FileInfo(path).Length <= 256 && File.ReadAllText(path) == fingerprint;
    }

    internal void MarkOffered(string fingerprint)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid review fingerprint.");
        Write(Path.Combine(_directory, "last-offered.txt"), System.Text.Encoding.UTF8.GetBytes(fingerprint), overwrite: true, protect: false);
    }

    private static void Validate(Consent consent)
    {
        if (consent.SchemaVersion != 1 || !Guid.TryParseExact(consent.Id, "N", out _) || consent.Plan is null || consent.SelectedIds is null)
            throw new InvalidDataException("The saved migration review is unsupported.");
        var plan = MigrationPlan.Create(consent.Plan.Items);
        if (plan.Fingerprint != consent.Plan.Fingerprint) throw new InvalidDataException("The migration review fingerprint changed.");
        var selection = new MigrationSelection(plan);
        foreach (var id in consent.SelectedIds) selection.Select(id);
        if (consent.SelectedIds.Length == 0 || consent.SelectedIds.Distinct().Count() != consent.SelectedIds.Length ||
            !selection.SelectedIds.SetEquals(consent.SelectedIds))
            throw new InvalidDataException("The migration review has an invalid selection.");
        if (!consent.CloudMaintenanceAcknowledged && plan.Items.Any(item => item.Area == MigrationArea.CloudPrefix && selection.SelectedIds.Contains(item.Id)))
            throw new InvalidDataException("Pause other clients using the selected cloud folders and confirm cloud maintenance before continuing.");
    }

    private static void Write(string path, byte[] bytes, bool overwrite, bool protect = true)
    {
        EnsureUnlinked(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var data = protect ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(data); output.Flush(true); }
            EnsureUnlinked(path);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void EnsureUnlinked(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The migration recovery path contains a linked file or folder: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
