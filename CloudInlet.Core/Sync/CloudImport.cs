using System.Security.Cryptography;
using System.Text;

namespace CloudInlet.Core.Sync;

public sealed record CloudImportItem(string RelativePath, CloudObject Source);
public sealed record CloudImportPlan(string JobId, string SourceBucketId, string SourcePrefix,
    string DestinationPath, IReadOnlyList<CloudImportItem> Items, long FileCount, long TotalBytes);
public sealed record CloudImportRecord(string Id, DateTimeOffset StartedUtc, CloudImportPlan Plan,
    string DestinationIdentity, string State, IReadOnlyDictionary<string, CloudObject> Completed, string? Error = null);

/// <summary>Validates a complete cloud source snapshot before any destination operation.</summary>
public static class CloudImport
{
    public static void ValidatePlan(CloudImportPlan plan)
    {
        if (plan is null || !Guid.TryParseExact(plan.JobId, "N", out _) || string.IsNullOrWhiteSpace(plan.SourceBucketId) ||
            plan.SourceBucketId.Length > 1024 || plan.SourcePrefix is null || plan.Items is null ||
            plan.FileCount < 0 || plan.TotalBytes < 0)
            throw new InvalidDataException("The cloud import checkpoint has missing or invalid source information. It was retained for review.");
        if (plan.SourcePrefix != PathRules.NormalizePrefix(plan.SourcePrefix) ||
            !IsCanonicalAbsolutePath(plan.DestinationPath))
            throw new InvalidDataException("The cloud import checkpoint has an unexpected source prefix or destination. Review it again.");
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var item in plan.Items)
        {
            if (item is null || item.Source is null || string.IsNullOrWhiteSpace(item.Source.FileId) ||
                item.Source.FileId.Length > 1024 || item.Source.Key is null || item.Source.Action != "upload" ||
                item.Source.Size < 0 || item.RelativePath is null || !versions.Add(item.Source.FileId))
                throw new InvalidDataException("The cloud import checkpoint contains ambiguous or invalid immutable file versions.");
            ValidateChecksum(item.Source.Sha1);
            if (!IsKnownChecksum(item.Source.Sha1))
                throw new InvalidDataException("This cloud file has no complete checksum. Download and import the source folder through Windows so its copied bytes can be verified.");
            var directory = item.Source.Key.EndsWith('/');
            var relative = PathRules.FromKey(directory ? item.Source.Key[..^1] : item.Source.Key, plan.SourcePrefix);
            if (relative.Split('/').Any(part => part.Length > 255) || PathRules.IsExcluded(relative, Array.Empty<string>()) ||
                item.RelativePath != relative + (directory ? "/" : "") || !items.Add(relative) || directory && item.Source.Size != 0)
                throw new InvalidDataException("The cloud import checkpoint contains an unsupported or conflicting Windows path.");
            if (!directory) { files.Add(relative); bytes = checked(bytes + item.Source.Size); }
        }
        foreach (var relative in items)
        {
            var parent = relative;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                if (files.Contains(parent)) throw new InvalidDataException("The cloud import checkpoint uses one path as both a file and a folder.");
            }
        }
        if (files.Count != plan.FileCount || bytes != plan.TotalBytes)
            throw new InvalidDataException("The cloud import checkpoint counts do not match its reviewed file versions.");
    }

    public static void ValidateCompleted(CloudImportPlan plan, IReadOnlyDictionary<string, CloudObject> completed,
        string destinationPrefix)
    {
        if (completed is null || destinationPrefix is null || destinationPrefix != PathRules.NormalizePrefix(destinationPrefix))
            throw new InvalidDataException("The cloud import completion checkpoint is invalid. It was retained for review.");
        var sourceByVersion = plan.Items.ToDictionary(item => item.Source.FileId, StringComparer.Ordinal);
        var copiedVersions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (sourceVersion, copied) in completed)
        {
            if (!sourceByVersion.TryGetValue(sourceVersion, out var item) || copied is null ||
                string.IsNullOrWhiteSpace(copied.FileId) || copied.FileId.Length > 1024 || !copiedVersions.Add(copied.FileId) || copied.Action != "upload" ||
                copied.Key != destinationPrefix + item.RelativePath || copied.Size != item.Source.Size ||
                copied.ModifiedUtc != item.Source.ModifiedUtc)
                throw new InvalidDataException("A completed cloud import receipt does not match its reviewed source version or destination.");
            ValidateChecksum(copied.Sha1);
            if (IsKnownChecksum(item.Source.Sha1) && !string.Equals(item.Source.Sha1, copied.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A completed cloud import receipt has a different checksum from its immutable source version.");
        }
    }

    private static bool IsKnownChecksum(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static void ValidateChecksum(string? value)
    {
        if (value is not null && !value.Equals("none", StringComparison.OrdinalIgnoreCase) && !IsKnownChecksum(value))
            throw new InvalidDataException("The import checkpoint contains a malformed file checksum.");
    }

    private static bool IsCanonicalAbsolutePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && !path.Split(['/', '\\']).Any(part => part is "." or "..") &&
        string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);

    public static async Task<CloudImportPlan> PreviewAsync(ICloudStore cloud, string sourceBucketId, string sourcePrefix,
        string destinationPath, CancellationToken ct = default)
    {
        sourcePrefix = PathRules.NormalizePrefix(sourcePrefix);
        var items = new Dictionary<string, CloudImportItem>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        await foreach (var source in cloud.ListCurrentAsync(sourceBucketId, sourcePrefix, ct))
        {
            if (source.Action != "upload" || source.Key == sourcePrefix) continue;
            var isDirectory = source.Key.EndsWith('/');
            var relative = PathRules.FromKey(isDirectory ? source.Key[..^1] : source.Key, sourcePrefix);
            if (relative.Split('/').Any(part => part.Length > 255))
                throw new InvalidDataException("The source contains a filename longer than Windows supports. Choose a smaller folder or rename it in the source cloud.");
            if (PathRules.IsExcluded(relative, Array.Empty<string>()))
                throw new InvalidDataException("The source contains CloudInlet's internal working folders. Choose only your personal files to import.");
            if (source.Size < 0 || isDirectory && source.Size != 0)
                throw new InvalidDataException("This cloud object cannot be represented as a Windows file or folder.");
            if (string.IsNullOrWhiteSpace(source.FileId) || !items.TryAdd(relative, new(relative + (isDirectory ? "/" : ""), source)))
                throw new InvalidDataException("The source contains ambiguous names or invalid file versions. Review the source before importing.");
            if (!isDirectory) { files.Add(relative); bytes = checked(bytes + source.Size); }
        }
        foreach (var relative in items.Keys)
        {
            var parent = relative;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                if (files.Contains(parent)) throw new InvalidDataException("The source uses one path as both a file and a folder. Resolve that conflict before importing into Windows.");
            }
        }
        var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), sourceBucketId, sourcePrefix,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath)),
            items.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(), files.Count, bytes);
        ValidatePlan(plan);
        return plan;
    }

    public static string OperationId(string jobId, CloudImportItem item, string destinationKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId + "|" + item.Source.FileId + "|" + destinationKey))).ToLowerInvariant();
}
