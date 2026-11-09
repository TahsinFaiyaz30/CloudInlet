using System.Text.Json;
using CloudInlet.Core.Migration;

namespace CloudInlet.Core.Sync;

public sealed partial class SyncManifest
{
    /// <summary>Remaps one quiesced root's verified immutable cloud versions in a single SQLite transaction.
    /// The caller must hold migration ownership, preserve native files before unregistering, and durably
    /// coordinate the settings/registration switch. This method alone does not migrate a cloud root.</summary>
    public void MigrateCloudPrefixReferences(string sourcePrefix, string destinationPrefix,
        IReadOnlyList<CloudPrefixVersionMapping> mappings, IReadOnlyList<CloudPrefixRule>? rules = null)
    {
        if (string.IsNullOrEmpty(sourcePrefix) || string.IsNullOrEmpty(destinationPrefix) ||
            PathRules.NormalizePrefix(sourcePrefix) != sourcePrefix || PathRules.NormalizePrefix(destinationPrefix) != destinationPrefix ||
            sourcePrefix.StartsWith(destinationPrefix, StringComparison.Ordinal) || destinationPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new ArgumentException("Cloud migration prefixes must be normalized and separate.");
        ArgumentNullException.ThrowIfNull(mappings);
        var keyMap = new CloudPrefixKeyMap(sourcePrefix, destinationPrefix, rules);
        keyMap.ValidateDistinctKeys(mappings.Select(value => value.Original.Key));
        var original = new Dictionary<string, CloudPrefixVersionMapping>(StringComparer.Ordinal);
        var migrated = new Dictionary<string, CloudPrefixVersionMapping>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (mapping is not { Original: { } before, Migrated: { } after } ||
                string.IsNullOrWhiteSpace(before.FileId) || string.IsNullOrWhiteSpace(after.FileId) || before.FileId == after.FileId ||
                !before.Key.StartsWith(sourcePrefix, StringComparison.Ordinal) || before.Key.Length <= sourcePrefix.Length ||
                after.Key != keyMap.Map(before.Key) || before.Size != after.Size || before.Size < 0 ||
                before.Action is not ("upload" or "hide") || before.Action != after.Action ||
                !string.Equals(before.Sha1, after.Sha1, StringComparison.OrdinalIgnoreCase) ||
                !original.TryAdd(before.FileId, mapping) || !migrated.TryAdd(after.FileId, mapping))
                throw new InvalidDataException("Cloud version mappings do not preserve unique reviewed source versions.");
        }
        if (original.Keys.Any(migrated.ContainsKey))
            throw new InvalidDataException("A migrated version cannot reuse any original version identity.");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "files", "directories" })
        {
            // All rows are read under the same transaction before they are updated.
            // Missing mappings or unknown versions abort the entire transaction.
            var updates = new List<(string Path, CloudObject Remote)>();
            using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT path,remote FROM " + table;
                using var rows = query.ExecuteReader();
                while (rows.Read())
                {
                    var remote = JsonSerializer.Deserialize<CloudObject>(rows.GetString(1))
                        ?? throw new InvalidDataException("A saved cloud version is unreadable.");
                    CloudPrefixVersionMapping mapping;
                    CloudPrefixVersion expected;
                    if (remote.Key.StartsWith(sourcePrefix, StringComparison.Ordinal) && original.TryGetValue(remote.FileId, out mapping!))
                        expected = mapping.Original;
                    else if (remote.Key.StartsWith(destinationPrefix, StringComparison.Ordinal) && migrated.TryGetValue(remote.FileId, out mapping!))
                        expected = mapping.Migrated;
                    else throw new InvalidDataException("A saved sync version has no verified migration mapping. The original manifest was retained.");
                    if (remote.Key != expected.Key || remote.Action != expected.Action || remote.Size != expected.Size ||
                        !string.Equals(remote.Sha1, expected.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A saved sync baseline changed after the cloud migration review.");
                    // Keep original content timestamps and local baseline fields; a server-side
                    // copy's upload time must not make unchanged files appear to be new content.
                    updates.Add((rows.GetString(0), remote with { FileId = mapping.Migrated.FileId, Key = mapping.Migrated.Key }));
                }
            }
            foreach (var update in updates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE " + table + " SET remote=$remote WHERE path=$path";
                command.Parameters.AddWithValue("$remote", JsonSerializer.Serialize(update.Remote));
                command.Parameters.AddWithValue("$path", update.Path);
                command.ExecuteNonQuery();
            }
        }
        using (var recovery = connection.CreateCommand())
        {
            recovery.Transaction = transaction;
            recovery.CommandText = "INSERT OR IGNORE INTO pending_native_marks(path) SELECT path FROM files; " +
                "INSERT OR IGNORE INTO registration_recovery(path) SELECT 'F:' || path FROM files; " +
                "INSERT OR IGNORE INTO registration_recovery(path) SELECT 'D:' || path FROM directories;";
            recovery.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
