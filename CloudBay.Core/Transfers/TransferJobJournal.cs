using System.Text.Json;
using CloudBay.Core.Sync;
using Microsoft.Data.Sqlite;

namespace CloudBay.Core.Transfers;

internal sealed record JournalItem(TransferEntry Entry, TransferItemState State, long Bytes,
    TransferCheckpoint? Checkpoint, TransferReceipt? Receipt, string? Error, bool Verified = false, bool DeleteStarted = false);
internal sealed record JournalJob(TransferJobPlan Plan, TransferJobState State, bool DiscoveryComplete, string? Cursor);

/// <summary>Metadata-only durable queue. Payload streams never enter this database.</summary>
public sealed class TransferJobJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly ITransferCheckpointProtector _protector;
    private bool _disposed;
    public IReadOnlyList<string> RecoverableJobIds { get; }

    public TransferJobJournal(string path, ITransferCheckpointProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS transfer_jobs(
                id TEXT PRIMARY KEY, plan TEXT NOT NULL, state INTEGER NOT NULL,
                discovery_complete INTEGER NOT NULL DEFAULT 0, cursor TEXT,
                file_count INTEGER NOT NULL DEFAULT 0, total_bytes INTEGER NOT NULL DEFAULT 0,
                completed_files INTEGER NOT NULL DEFAULT 0, skipped_files INTEGER NOT NULL DEFAULT 0,
                transferred_bytes INTEGER NOT NULL DEFAULT 0, settled_bytes INTEGER NOT NULL DEFAULT 0, skipped_bytes INTEGER NOT NULL DEFAULT 0, error TEXT);
            CREATE TABLE IF NOT EXISTS transfer_items(
                job_id TEXT NOT NULL, id TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE,
                entry TEXT NOT NULL, state INTEGER NOT NULL, bytes INTEGER NOT NULL DEFAULT 0,
                checkpoint BLOB, receipt TEXT, error TEXT, verified INTEGER NOT NULL DEFAULT 0, delete_started INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(job_id,id), UNIQUE(job_id,path));
            CREATE INDEX IF NOT EXISTS transfer_queue ON transfer_items(job_id,state);
            PRAGMA user_version=1;
            """);
        using (var columns = Command("PRAGMA table_info(transfer_jobs)"))
        {
            var hasSkipped = false;
            using (var reader = columns.ExecuteReader())
                while (reader.Read()) if (reader.GetString(1) == "skipped_bytes") hasSkipped = true;
            if (!hasSkipped)
                Execute("ALTER TABLE transfer_jobs ADD COLUMN skipped_bytes INTEGER NOT NULL DEFAULT 0; " +
                    "UPDATE transfer_jobs SET skipped_bytes=COALESCE((SELECT SUM(json_extract(entry,'$.Size')-bytes) FROM transfer_items WHERE job_id=transfer_jobs.id AND state=$skipped),0)",
                    ("$skipped", (int)TransferItemState.Skipped));
        }
        using (var saved = Command("SELECT id FROM transfer_jobs WHERE state IN ($discovering,$running)",
            ("$discovering", (int)TransferJobState.Discovering), ("$running", (int)TransferJobState.Running)))
        {
            using var reader = saved.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            RecoverableJobIds = ids;
        }
        // A process exit cannot leave a durable claim permanently assigned to a dead worker.
        Execute("UPDATE transfer_jobs SET state=$paused WHERE state IN ($discovering,$running); " +
            "UPDATE transfer_items SET state=$queued WHERE state=$transferring OR (state=$verifying AND receipt IS NULL)",
            ("$paused", (int)TransferJobState.Paused), ("$discovering", (int)TransferJobState.Discovering),
            ("$running", (int)TransferJobState.Running), ("$queued", (int)TransferItemState.Queued),
            ("$transferring", (int)TransferItemState.Transferring), ("$verifying", (int)TransferItemState.Verifying));
    }

    public void Create(TransferJobPlan plan)
    {
        TransferValidation.ValidatePlan(plan);
        lock (_gate) Execute("INSERT INTO transfer_jobs(id,plan,state) VALUES($id,$plan,$state)",
            ("$id", plan.Id), ("$plan", JsonSerializer.Serialize(plan)), ("$state", (int)TransferJobState.Discovering));
    }

    internal JournalJob GetJob(string jobId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT plan,state,discovery_complete,cursor FROM transfer_jobs WHERE id=$id", ("$id", jobId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("The transfer job was not found.");
            var plan = JsonSerializer.Deserialize<TransferJobPlan>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid transfer plan.");
            TransferValidation.ValidatePlan(plan);
            if (plan.Id != jobId || !Enum.IsDefined((TransferJobState)reader.GetInt32(1))) throw new InvalidDataException("Invalid saved transfer identity or state.");
            return new(plan, (TransferJobState)reader.GetInt32(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    internal void SaveDiscovery(string jobId, TransferDiscoveryPage page)
    {
        lock (_gate)
        {
            var job = GetJob(jobId);
            if (job.DiscoveryComplete) throw new InvalidOperationException("Discovery is already complete.");
            if (page.Entries is null || page.Entries.Count > 10_000 || page.NextCursor is { Length: > 1_048_576 } ||
                page.NextCursor is not null && page.NextCursor == job.Cursor)
                throw new InvalidDataException("The provider returned an invalid discovery page or a repeated cursor.");
            foreach (var entry in page.Entries) TransferValidation.ValidateEntry(entry);
            using var transaction = _connection.BeginTransaction();
            long count = 0, bytes = 0;
            foreach (var entry in page.Entries)
            {
                if (PathRules.IsExcluded(entry.RelativePath.TrimEnd('/'), job.Plan.Exclusions)) continue;
                using var command = Command("INSERT INTO transfer_items(job_id,id,path,entry,state) VALUES($job,$id,$path,$entry,$state)",
                    ("$job", jobId), ("$id", entry.Id), ("$path", entry.RelativePath.TrimEnd('/')),
                    ("$entry", JsonSerializer.Serialize(entry)), ("$state", (int)TransferItemState.Queued));
                command.Transaction = transaction;
                try { command.ExecuteNonQuery(); }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    using var existing = Command("SELECT entry FROM transfer_items WHERE job_id=$job AND (id=$id OR path=$path)",
                        ("$job", jobId), ("$id", entry.Id), ("$path", entry.RelativePath.TrimEnd('/')));
                    existing.Transaction = transaction;
                    using var saved = existing.ExecuteReader();
                    if (saved.Read() && JsonSerializer.Deserialize<TransferEntry>(saved.GetString(0)) == entry && !saved.Read())
                        continue; // An expired discovery page may replay unchanged saved identities.
                    throw new InvalidDataException("The source listing changed or contains a conflicting identity or destination path. The page was not committed.", exception);
                }
                if (!entry.IsFolder) { count++; bytes = checked(bytes + entry.Size); }
            }
            using var update = Command("UPDATE transfer_jobs SET cursor=$cursor,discovery_complete=$complete,file_count=file_count+$count,total_bytes=total_bytes+$bytes WHERE id=$job",
                ("$cursor", page.NextCursor), ("$complete", page.NextCursor is null ? 1 : 0), ("$count", count), ("$bytes", bytes), ("$job", jobId));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    internal JournalItem? Claim(string jobId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT entry,state,bytes,checkpoint,receipt,error,verified,delete_started FROM transfer_items WHERE job_id=$job AND state=$queued ORDER BY rowid LIMIT 1",
                ("$job", jobId), ("$queued", (int)TransferItemState.Queued));
            JournalItem? item;
            using (var reader = command.ExecuteReader()) item = reader.Read() ? ReadItem(reader) : null;
            if (item is null) return null;
            Execute("UPDATE transfer_items SET state=$state,error=NULL WHERE job_id=$job AND id=$id",
                ("$state", (int)TransferItemState.Transferring), ("$job", jobId), ("$id", item.Entry.Id));
            return item with { Error = null };
        }
    }

    internal IReadOnlyList<JournalItem> PendingVerification(string jobId, int limit)
    {
        lock (_gate)
        {
            using var command = Command("SELECT entry,state,bytes,checkpoint,receipt,error,verified,delete_started FROM transfer_items WHERE job_id=$job AND state IN ($verify,$verified,$deleting) ORDER BY rowid LIMIT $limit",
                ("$job", jobId), ("$verify", (int)TransferItemState.Verifying), ("$verified", (int)TransferItemState.Verified),
                ("$deleting", (int)TransferItemState.DeletingSource), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var list = new List<JournalItem>();
            while (reader.Read()) list.Add(ReadItem(reader));
            return list;
        }
    }

    internal void SaveCheckpoint(string jobId, TransferEntry entry, TransferCheckpoint checkpoint)
    {
        if (checkpoint.AcknowledgedBytes < 0 || checkpoint.AcknowledgedBytes > entry.Size || string.IsNullOrWhiteSpace(checkpoint.Provider) ||
            checkpoint.SessionId is null || checkpoint.Data?.Count > 10_000) throw new InvalidDataException("Invalid acknowledged transfer checkpoint.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        byte[] encrypted;
        try { encrypted = _protector.Protect(plaintext); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var updateJob = Command("UPDATE transfer_jobs SET transferred_bytes=transferred_bytes+$bytes-(SELECT bytes FROM transfer_items WHERE job_id=$job AND id=$id) WHERE id=$job",
                ("$bytes", checkpoint.AcknowledgedBytes), ("$job", jobId), ("$id", entry.Id));
            updateJob.Transaction = transaction; updateJob.ExecuteNonQuery();
            using var update = Command("UPDATE transfer_items SET checkpoint=$checkpoint,bytes=$bytes WHERE job_id=$job AND id=$id",
                ("$checkpoint", encrypted), ("$bytes", checkpoint.AcknowledgedBytes), ("$job", jobId), ("$id", entry.Id));
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("The checkpoint has no planned source item.");
            transaction.Commit();
        }
    }

    internal void SaveReceipt(string jobId, TransferEntry entry, TransferReceipt receipt, string operationId)
    {
        TransferValidation.ValidateReceipt(entry, receipt, operationId);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var updateJob = Command("UPDATE transfer_jobs SET transferred_bytes=transferred_bytes+$size-(SELECT bytes FROM transfer_items WHERE job_id=$job AND id=$id) WHERE id=$job",
                ("$size", entry.Size), ("$job", jobId), ("$id", entry.Id));
            updateJob.Transaction = transaction; updateJob.ExecuteNonQuery();
            using var update = Command("UPDATE transfer_items SET receipt=$receipt,state=$state,bytes=$size,error=NULL WHERE job_id=$job AND id=$id",
                ("$receipt", JsonSerializer.Serialize(receipt)), ("$state", (int)TransferItemState.Verifying),
                ("$size", entry.Size), ("$job", jobId), ("$id", entry.Id));
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("The receipt has no planned source item.");
            transaction.Commit();
        }
    }

    internal void SetItemState(string jobId, string id, TransferItemState state, string? error = null)
    {
        lock (_gate) Execute("UPDATE transfer_items SET state=$state,error=$error," +
            "verified=CASE WHEN $state IN ($verified,$deleting) THEN 1 ELSE verified END," +
            "delete_started=CASE WHEN $state=$deleting THEN 1 ELSE delete_started END WHERE job_id=$job AND id=$id",
            ("$state", (int)state), ("$error", error), ("$job", jobId), ("$id", id),
            ("$verified", (int)TransferItemState.Verified), ("$deleting", (int)TransferItemState.DeletingSource));
    }

    internal void Finish(string jobId, TransferEntry entry, bool skipped = false)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var item = Command("UPDATE transfer_items SET state=$state,error=NULL,verified=CASE WHEN $state=$complete THEN 1 ELSE verified END WHERE job_id=$job AND id=$id AND state NOT IN ($complete,$skipped)",
                ("$state", (int)(skipped ? TransferItemState.Skipped : TransferItemState.Completed)), ("$job", jobId), ("$id", entry.Id),
                ("$complete", (int)TransferItemState.Completed), ("$skipped", (int)TransferItemState.Skipped));
            item.Transaction = transaction;
            if (item.ExecuteNonQuery() == 1)
            {
                using var job = Command("UPDATE transfer_jobs SET completed_files=completed_files+$completed,skipped_files=skipped_files+$skipped,settled_bytes=settled_bytes+$size," +
                    "skipped_bytes=skipped_bytes+CASE WHEN $isSkipped=1 THEN $size-(SELECT bytes FROM transfer_items WHERE job_id=$job AND id=$id) ELSE 0 END WHERE id=$job",
                    ("$completed", !skipped && !entry.IsFolder ? 1 : 0), ("$skipped", skipped && !entry.IsFolder ? 1 : 0),
                    ("$isSkipped", skipped ? 1 : 0), ("$size", entry.Size), ("$job", jobId), ("$id", entry.Id));
                job.Transaction = transaction; job.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    internal void SetState(string jobId, TransferJobState state, string? error = null)
    {
        lock (_gate) Execute("UPDATE transfer_jobs SET state=$state,error=$error WHERE id=$job",
            ("$state", (int)state), ("$error", error), ("$job", jobId));
    }

    internal void ResetUnfinished(string jobId, bool retryFailures)
    {
        lock (_gate) Execute("UPDATE transfer_items SET state=$queued,error=NULL WHERE job_id=$job AND " +
            "(state IN ($transferring,$verifying,$verified,$deleting) OR ($retry=1 AND state=$attention))",
            ("$queued", (int)TransferItemState.Queued), ("$job", jobId), ("$transferring", (int)TransferItemState.Transferring),
            ("$verifying", (int)TransferItemState.Verifying), ("$verified", (int)TransferItemState.Verified),
            ("$deleting", (int)TransferItemState.DeletingSource), ("$retry", retryFailures ? 1 : 0), ("$attention", (int)TransferItemState.Attention));
    }

    internal bool HasUnfinished(string jobId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT EXISTS(SELECT 1 FROM transfer_items WHERE job_id=$job AND state NOT IN ($completed,$skipped))",
                ("$job", jobId), ("$completed", (int)TransferItemState.Completed), ("$skipped", (int)TransferItemState.Skipped));
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        }
    }

    public IReadOnlyList<TransferJobSnapshot> Snapshots()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id FROM transfer_jobs ORDER BY rowid DESC");
            var ids = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(Snapshot).ToArray();
        }
    }

    public TransferJobSnapshot Snapshot(string jobId)
    {
        lock (_gate)
        {
            var job = GetJob(jobId);
            using var command = Command("SELECT file_count,total_bytes,completed_files,skipped_files,transferred_bytes,settled_bytes,error," +
                "(SELECT COUNT(*) FROM transfer_items WHERE job_id=$job AND state=$queued),skipped_bytes FROM transfer_jobs WHERE id=$job",
                ("$job", jobId), ("$queued", (int)TransferItemState.Queued));
            long count, total, completed, skipped, transferred, settled, queued, skippedBytes;
            string? error;
            using (var reader = command.ExecuteReader())
            {
                reader.Read(); count = reader.GetInt64(0); total = reader.GetInt64(1); completed = reader.GetInt64(2);
                skipped = reader.GetInt64(3); transferred = reader.GetInt64(4); settled = reader.GetInt64(5);
                error = reader.IsDBNull(6) ? null : reader.GetString(6); queued = reader.GetInt64(7); skippedBytes = reader.GetInt64(8);
            }
            if (count < 0 || total < 0 || completed < 0 || skipped < 0 || completed + skipped > count || transferred < 0 || transferred > total || settled < 0 || settled > total || skippedBytes < 0 || skippedBytes > total - transferred)
                throw new InvalidDataException("Saved transfer counters are inconsistent; the journal was retained for review.");
            using var items = Command("SELECT entry,state,bytes,error FROM transfer_items WHERE job_id=$job AND state NOT IN ($complete,$skipped) " +
                "ORDER BY CASE WHEN state=$queued THEN 1 ELSE 0 END,rowid LIMIT 256", ("$job", jobId),
                ("$complete", (int)TransferItemState.Completed), ("$skipped", (int)TransferItemState.Skipped), ("$queued", (int)TransferItemState.Queued));
            using var itemReader = items.ExecuteReader();
            var window = new List<TransferItemSnapshot>();
            while (itemReader.Read())
            {
                var entry = JsonSerializer.Deserialize<TransferEntry>(itemReader.GetString(0)) ?? throw new InvalidDataException("Invalid saved source item.");
                TransferValidation.ValidateEntry(entry);
                var state = (TransferItemState)itemReader.GetInt32(1);
                var bytes = itemReader.GetInt64(2);
                if (!Enum.IsDefined(state) || bytes < 0 || bytes > entry.Size) throw new InvalidDataException("Invalid saved item progress.");
                window.Add(new(entry.Id, entry.RelativePath, state, bytes, entry.Size, 0, itemReader.IsDBNull(3) ? null : itemReader.GetString(3)));
            }
            return new(job.Plan, job.State, job.DiscoveryComplete, count, completed, skipped, total, transferred,
                total - transferred - skippedBytes, queued, 0, window, error);
        }
    }

    private JournalItem ReadItem(SqliteDataReader reader)
    {
        var entry = JsonSerializer.Deserialize<TransferEntry>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid saved source item.");
        TransferValidation.ValidateEntry(entry);
        var state = (TransferItemState)reader.GetInt32(1);
        var bytes = reader.GetInt64(2);
        if (!Enum.IsDefined(state) || bytes < 0 || bytes > entry.Size) throw new InvalidDataException("Invalid saved item progress.");
        TransferCheckpoint? checkpoint = null;
        if (!reader.IsDBNull(3))
        {
            var plaintext = _protector.Unprotect((byte[])reader[3]);
            try { checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(plaintext) ?? throw new InvalidDataException("Invalid saved upload checkpoint."); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
            if (checkpoint.AcknowledgedBytes < 0 || checkpoint.AcknowledgedBytes > entry.Size)
                throw new InvalidDataException("The saved acknowledged bytes exceed the source length.");
        }
        var receipt = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<TransferReceipt>(reader.GetString(4));
        if (receipt is not null) TransferValidation.ValidateReceipt(entry, receipt, receipt.OperationId);
        return new(entry, state, bytes, checkpoint, receipt, reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7));
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters); command.ExecuteNonQuery();
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _connection.Dispose(); _disposed = true; } }
}

public static class TransferValidation
{
    public static void ValidatePlan(TransferJobPlan plan)
    {
        if (plan is null || !Guid.TryParseExact(plan.Id, "N", out _) || plan.Exclusions is null ||
            !Enum.IsDefined(plan.Operation) || !Enum.IsDefined(plan.ConflictPolicy) || plan.Exclusions.Count > 4096 ||
            plan.Exclusions.Any(value => value is null || value.Length > 4096 || value.Contains("..") || value.Contains(':')))
            throw new InvalidDataException("The transfer plan has an invalid identity, operation, or exclusions.");
        ValidateLocation(plan.Source); ValidateLocation(plan.Destination);
        if (plan.Source.Provider == plan.Destination.Provider && plan.Source.AccountId == plan.Destination.AccountId &&
            plan.Source.ContainerId == plan.Destination.ContainerId)
        {
            var source = plan.Source.Path.Replace('\\', '/').TrimEnd('/') + "/";
            var destination = plan.Destination.Path.Replace('\\', '/').TrimEnd('/') + "/";
            if (plan.Source.FolderId.Length > 0 && plan.Source.FolderId == plan.Destination.FolderId ||
                source == "/" || destination == "/" || source.StartsWith(destination, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose source and destination folders that do not overlap.");
        }
    }
    public static void ValidateLocation(TransferLocation location)
    {
        if (location is null || location.Provider is not ("local" or "b2" or "onedrive") ||
            string.IsNullOrWhiteSpace(location.AccountId) || string.IsNullOrWhiteSpace(location.ContainerId) ||
            location.FolderId is null || location.Path is null || string.IsNullOrWhiteSpace(location.DisplayName) ||
            location.Path.Length > 32768 || location.AccountId.Length > 1024 || location.ContainerId.Length > 1024 || location.FolderId.Length > 1024)
            throw new InvalidDataException("A transfer endpoint has missing or invalid account or folder information.");
    }
    public static void ValidateEntry(TransferEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 4096 || string.IsNullOrWhiteSpace(entry.Version) ||
            entry.Version.Length > 4096 || entry.RelativePath is null || entry.Size < 0 || entry.IsFolder && entry.Size != 0)
            throw new InvalidDataException("Invalid source file identity or version.");
        PathRules.ValidateRelative(entry.RelativePath.TrimEnd('/'));
        if (entry.Sha1 is not null && (entry.Sha1.Length != 40 || !entry.Sha1.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The source contains a malformed checksum.");
    }
    public static void ValidateReceipt(TransferEntry entry, TransferReceipt receipt, string operationId)
    {
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.Id) || string.IsNullOrWhiteSpace(receipt.Version) ||
            receipt.OperationId != operationId || receipt.Size != entry.Size || string.IsNullOrWhiteSpace(receipt.OperationId))
            throw new InvalidDataException("The destination receipt does not match its planned file and operation.");
        PathRules.ValidateRelative(receipt.RelativePath.TrimEnd('/'));
        if (receipt.Sha1 is not null && (receipt.Sha1.Length != 40 || !receipt.Sha1.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The destination receipt has an invalid checksum.");
        if (entry.Sha1 is not null && receipt.Sha1 is not null && !entry.Sha1.Equals(receipt.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination checksum differs from the source.");
    }
}
