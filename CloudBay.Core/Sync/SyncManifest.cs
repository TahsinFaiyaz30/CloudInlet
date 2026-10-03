using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudBay.Core.Sync;

public sealed record SyncEntry(string RelativePath, CloudObject Remote, long LocalSize, DateTimeOffset LocalWriteUtc);
public sealed record SyncDirectoryEntry(string RelativePath, CloudObject Remote);

public sealed class SyncManifest
{
    private readonly string _connectionString;
    public SyncManifest(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS files (path TEXT PRIMARY KEY COLLATE NOCASE, remote TEXT NOT NULL, local_size INTEGER NOT NULL, local_write TEXT NOT NULL); CREATE TABLE IF NOT EXISTS directories (path TEXT PRIMARY KEY COLLATE NOCASE, remote TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public IReadOnlyDictionary<string, SyncEntry> ReadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,remote,local_size,local_write FROM files";
        using var reader = command.ExecuteReader();
        var entries = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var path = reader.GetString(0);
            entries.Add(path, new(path, JsonSerializer.Deserialize<CloudObject>(reader.GetString(1))
                ?? throw new InvalidDataException("Sync state could not be read."), reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return entries;
    }

    public void Put(SyncEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO files(path,remote,local_size,local_write) VALUES($path,$remote,$size,$write) ON CONFLICT(path) DO UPDATE SET remote=$remote,local_size=$size,local_write=$write";
        command.Parameters.AddWithValue("$path", entry.RelativePath);
        command.Parameters.AddWithValue("$remote", JsonSerializer.Serialize(entry.Remote));
        command.Parameters.AddWithValue("$size", entry.LocalSize);
        command.Parameters.AddWithValue("$write", entry.LocalWriteUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, SyncDirectoryEntry> ReadDirectories()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,remote FROM directories";
        using var reader = command.ExecuteReader();
        var entries = new Dictionary<string, SyncDirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var path = reader.GetString(0);
            entries.Add(path, new(path, JsonSerializer.Deserialize<CloudObject>(reader.GetString(1))
                ?? throw new InvalidDataException("Directory sync state could not be read.")));
        }
        return entries;
    }

    public void PutDirectory(SyncDirectoryEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO directories(path,remote) VALUES($path,$remote) ON CONFLICT(path) DO UPDATE SET remote=$remote";
        command.Parameters.AddWithValue("$path", entry.RelativePath);
        command.Parameters.AddWithValue("$remote", JsonSerializer.Serialize(entry.Remote));
        command.ExecuteNonQuery();
    }

    public void RemoveDirectory(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM directories WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }
}
