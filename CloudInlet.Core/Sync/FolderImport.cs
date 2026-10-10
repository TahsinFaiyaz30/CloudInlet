namespace CloudInlet.Core.Sync;

public sealed record FolderImportPlan(string SourcePath, string DestinationPath, string Fingerprint,
    long FileCount, long TotalBytes, long? AvailableBytes, bool HasOnlineOnlyFiles);

/// <summary>An explicit copy into native storage. Sources are never removed or redirected.</summary>
public static class FolderImport
{
    public static FolderImportPlan Preview(string source, string destination, CancellationToken ct = default)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (IsWithin(source, destination) || IsWithin(destination, source))
            throw new IOException("Choose separate source and destination folders, with neither inside the other.");
        var inspection = VerifiedTreeCopy.Inspect(source, ct);
        long? available = null;
        try
        {
            var root = Path.GetPathRoot(destination)!;
            var drive = new DriveInfo(root);
            if (drive.IsReady) available = drive.AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (available is { } bytes && bytes < inspection.TotalBytes)
            throw new IOException("The destination drive does not have enough space for this import. Make space or choose a smaller source folder.");
        return new(source, destination, inspection.Fingerprint, inspection.FileCount,
            inspection.TotalBytes, available, inspection.HasOnlineOnlyFiles);
    }

    public static async Task<string> ExecuteAsync(FolderImportPlan reviewed, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        // Preview is never a permission to apply a different snapshot later. Files can still
        // change during copying; VerifiedTreeCopy checks held handles and the final tree too.
        var current = Preview(reviewed.SourcePath, reviewed.DestinationPath, ct);
        if (!current.Fingerprint.Equals(reviewed.Fingerprint, StringComparison.Ordinal))
            throw new IOException("The source changed since you reviewed it. Review the import again; no new copies were started.");
        return await VerifiedTreeCopy.CopyVerifiedAsync(current.SourcePath, current.DestinationPath, ct, progress,
            reviewedFingerprint: reviewed.Fingerprint);
    }

    private static bool IsWithin(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
