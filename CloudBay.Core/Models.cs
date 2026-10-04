namespace CloudBay.Core;

public sealed record B2Credentials(string KeyId, string ApplicationKey);
public sealed record CloudAccount(string AccountId, string ApiUrl, string DownloadUrl,
    IReadOnlyList<string> Capabilities, string? AllowedBucketId = null, string? AllowedNamePrefix = null);
public sealed record CloudBucket(string Id, string Name);
public sealed record CloudObject(string FileId, string Key, long Size, string? Sha1,
    DateTimeOffset ModifiedUtc, string Action = "upload");
public sealed record TransferProgress(long Bytes, long TotalBytes)
{
    /// <summary>Bytes already confirmed before this request; they are not new network traffic.</summary>
    public bool IsBaseline { get; init; }
}
public sealed record DownloadChunk(long Offset, long Length, string Sha1);
public enum TransferPhase { Queued, Hashing, Uploading, Downloading, Verifying, Retrying, Paused }
public sealed record TransferSnapshot(string Id, string RootName, string RelativePath,
    ActivityKind Kind, TransferPhase Phase, long Bytes, long TotalBytes)
{
    public double BytesPerSecond { get; init; }
}
public enum UploadMode { Intelligent, MaximumThroughput, Manual }
public enum ActivityKind { Upload, Download, Delete, Restore, Conflict, Backup, Information, Error }
/// <summary>The exact root and B2 namespace that produced an event. Contains no credentials.</summary>
public sealed record ActivityLocation(string RootPath, string? BackupName, string RelativePath, string BucketId, string Prefix);
public sealed record ActivityEvent(DateTimeOffset Time, ActivityKind Kind, string Path, string Message,
    long Bytes = 0, bool Completed = true)
{
    public ActivityLocation? Location { get; init; }
}
public enum PinMode { OnlineOnly, Available, AlwaysAvailable }
public enum ClientState { NotConnected, Connecting, Syncing, UpToDate, Paused, Offline, Attention }
public sealed record SyncSnapshot(ClientState State, string Message, int Pending = 0,
    int FileCount = 0, long CloudBytes = 0, long LocalBytes = 0,
    long TransferredBytes = 0, long TransferTotalBytes = 0, DateTimeOffset? LastSync = null)
{
    public IReadOnlyList<TransferSnapshot> Transfers { get; init; } = [];
    public int ActiveTransfers { get; init; }
    public int QueuedTransfers { get; init; }
    public double UploadBytesPerSecond { get; init; }
    public double DownloadBytesPerSecond { get; init; }
}

public sealed record BackupFolder(string Name, string OriginalPath, string DestinationPath);
public sealed record CustomBackupFolder(string Name, string SourcePath, string Prefix);
/// <summary>A literal file or folder selection, scoped to one local sync root.</summary>
public sealed record SelectedExclusion(string RootPath, string RelativePath, bool IsFolder, bool Enabled = true);
public enum ExclusionTarget { Files, Folders, All }
/// <summary>A name or path pattern built by the UI, optionally scoped to one local root.</summary>
public sealed record GuidedExclusion(string Pattern, ExclusionTarget Target, string? RootPath = null,
    string? RelativeDirectory = null, bool Enabled = true);
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string KeyId { get; init; } = "";
    public string BucketId { get; init; } = "";
    public string BucketName { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string RootPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudBay");
    public string Prefix { get; init; } = "CloudBay/";
    public bool StartAtSignIn { get; init; } = true;
    public bool FilesOnDemand { get; init; } = true;
    public bool PauseOnMetered { get; init; } = true;
    public bool PauseOnBatterySaver { get; init; } = true;
    public int UploadConcurrency { get; init; } = 4;
    public int DownloadConcurrency { get; init; } = 4;
    public UploadMode UploadMode { get; init; } = UploadMode.Intelligent;
    public long UploadBytesPerSecond { get; init; }
    public long DownloadBytesPerSecond { get; init; }
    public int PollSeconds { get; init; } = 60;
    public string Theme { get; init; } = "System";
    public List<string> Exclusions { get; init; } = ["~$*"];
    /// <summary>Disabled legacy expressions retain their original syntax and matching semantics.</summary>
    public List<string> DisabledLegacyExclusions { get; init; } = [];
    public List<SelectedExclusion> SelectedExclusions { get; init; } = [];
    public List<GuidedExclusion> GuidedExclusions { get; init; } = [];
    public List<BackupFolder> Backups { get; init; } = [];
    public List<CustomBackupFolder> CustomBackups { get; init; } = [];
    public bool IsConfigured => BucketId.Length > 0 && KeyId.Length > 0;
}

public delegate Task HydrationHandler(CloudObject file, long offset, long length, Stream destination, CancellationToken cancellationToken);
