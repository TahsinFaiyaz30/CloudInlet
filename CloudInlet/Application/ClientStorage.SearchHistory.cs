using System.Security;
using System.Text.Json;

namespace CloudInlet.Application;

public sealed partial class ClientStorage
{
    private const int SearchHistoryLimit = 10;
    private const int SearchQueryLengthLimit = 256;
    private const int SearchHistoryFileSizeLimit = 32 * 1024;
    private readonly object _searchHistoryGate = new();

    internal IReadOnlyList<string> LoadSearchHistory()
    {
        lock (_searchHistoryGate)
        {
            try
            {
                var path = Path.Combine(DirectoryPath, "search-history.json");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > SearchHistoryFileSizeLimit) return [];
                var queries = JsonSerializer.Deserialize<string[]>(stream);
                return NormalizeSearchHistory(queries ?? []);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
            {
                // Recent searches are optional; a damaged or inaccessible history must not block navigation.
                return [];
            }
        }
    }

    internal void SaveSearchHistory(IReadOnlyList<string> queries)
    {
        lock (_searchHistoryGate)
        {
            var path = Path.Combine(DirectoryPath, "search-history.json");
            var temporary = path + ".tmp";
            var history = NormalizeSearchHistory(queries);
            if (history.Count == 0)
            {
                DeleteSearchHistoryFile(path);
                DeleteSearchHistoryFile(temporary);
                DeleteSearchHistoryFile(path + ".bak");
                return;
            }

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, history);
                    stream.Flush(flushToDisk: true);
                }
                // Keep only the current list, so clearing history cannot leave older searches in a backup.
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                // The in-memory history remains usable when persistence is unavailable.
            }
            finally { DeleteSearchHistoryFile(temporary); }
        }
    }

    private static IReadOnlyList<string> NormalizeSearchHistory(IReadOnlyList<string> queries)
    {
        var history = new List<string>(SearchHistoryLimit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in queries)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var query = value.Trim();
            // Preserve complete queries instead of recording an incomplete, potentially different search.
            if (query.Length > SearchQueryLengthLimit || !seen.Add(query)) continue;
            history.Add(query);
            if (history.Count == SearchHistoryLimit) break;
        }
        return history;
    }

    private static void DeleteSearchHistoryFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A locked or read-only file must not interrupt the user's current search.
        }
    }
}
