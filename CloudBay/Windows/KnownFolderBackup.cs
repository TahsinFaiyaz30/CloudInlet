using System.Runtime.InteropServices;
using CloudBay.Core;
using CloudBay.Core.Sync;

namespace CloudBay.Windows;

public sealed record BackupSourceReview(string Name, string OriginalWindowsPath,
    FolderImportPlan CurrentFiles, FolderImportPlan? AdditionalFiles, bool IsRedirected,
    BackupTransferMode TransferMode = BackupTransferMode.Copy, bool IncludeCurrentFiles = true)
{
    public string DestinationPath => CurrentFiles.DestinationPath;
}

public sealed record BackupRestoreReview(BackupFolder Folder, string DestinationPath,
    BackupTransferMode TransferMode, FolderImportPlan? Files);

public sealed record BackupApplyResult(BackupFolder Folder, string? RetentionWarning);

/// <summary>Opt-in Windows folder redirection with explicit reviewed copy, move, or no-transfer choices.</summary>
public static class KnownFolderBackup
{
    public static BackupSourceReview Preview(string name, string root, string? additionalSource = null, CancellationToken ct = default)
        => Preview(name, root, additionalSource, BackupTransferMode.Copy, true, ct);

    public static BackupSourceReview Preview(string name, string root, string? additionalSource,
        BackupTransferMode mode, bool includeCurrentFiles, CancellationToken ct = default)
    {
        ValidateMode(mode);
        ct.ThrowIfCancellationRequested();
        KnownFolderPolicy.EnsureRedirectable(GetId(name));
        var original = GetPath(name);
        var destination = PathRules.FullPath(root, name);
        return PreviewForPaths(name, original, destination, GetDefaultPath(name), additionalSource, mode, includeCurrentFiles, ct);
    }

    // Isolated tests use resolved fixture paths; production resolves and checks Shell policy above.
    internal static BackupSourceReview PreviewForPaths(string name, string original, string destination, string defaultPath,
        string? additionalSource, BackupTransferMode mode, bool includeCurrentFiles, CancellationToken ct = default)
    {
        ValidateMode(mode);
        ct.ThrowIfCancellationRequested();
        if (Path.GetFullPath(original).Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This Windows folder already points at CloudBay. Recover its backup record before changing its location.");
        EnsureSeparatePaths(original, destination);
        if (mode == BackupTransferMode.None) includeCurrentFiles = false;
        var current = !includeCurrentFiles ? new FolderImportPlan(original, destination, "not-selected", 0, 0, null, false) :
            Directory.Exists(original) ? FolderImport.Preview(original, destination, ct) :
            new FolderImportPlan(original, destination, "missing", 0, 0, null, false);
        var extra = mode == BackupTransferMode.None || string.IsNullOrWhiteSpace(additionalSource) ||
            (includeCurrentFiles && Path.GetFullPath(additionalSource).Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase))
            ? null : FolderImport.Preview(additionalSource, destination, ct);
        if (mode != BackupTransferMode.None && !includeCurrentFiles && extra is null)
            throw new IOException("Choose a folder to copy or move, or choose to start without importing files.");
        if (extra is not null && current.AvailableBytes is { } available && available < checked(current.TotalBytes + extra.TotalBytes))
            throw new IOException("The destination drive does not have enough space for both selected sources.");
        return new(name, original, current, extra,
            !Path.GetFullPath(original).Equals(defaultPath, StringComparison.OrdinalIgnoreCase), mode, includeCurrentFiles);
    }

    public static async Task<BackupFolder> EnableReviewedAsync(BackupSourceReview reviewed, CancellationToken ct)
        => (await ApplyEnableReviewedAsync(reviewed, ct)).Folder;

    public static async Task<BackupApplyResult> ApplyEnableReviewedAsync(BackupSourceReview reviewed, CancellationToken ct)
    {
        ValidateMode(reviewed.TransferMode);
        KnownFolderPolicy.EnsureRedirectable(GetId(reviewed.Name));
        return await ApplyEnableForPathsAsync(reviewed, () => GetPath(reviewed.Name), path => SetPath(reviewed.Name, path), ct);
    }

    internal static async Task<BackupApplyResult> ApplyEnableForPathsAsync(BackupSourceReview reviewed,
        Func<string> getCurrentPath, Action<string> setPath, CancellationToken ct = default)
    {
        ValidateMode(reviewed.TransferMode);
        if (!getCurrentPath().Equals(reviewed.OriginalWindowsPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another app changed this Windows folder location. Review the source again; the mapping was retained.");
        EnsureSeparatePaths(reviewed.OriginalWindowsPath, reviewed.DestinationPath);
        if (reviewed.TransferMode != BackupTransferMode.None && reviewed.IncludeCurrentFiles &&
            !reviewed.CurrentFiles.SourcePath.Equals(reviewed.OriginalWindowsPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The current Windows folder source changed. Review the backup again.");
        if (reviewed.AdditionalFiles is { } selected && !selected.DestinationPath.Equals(reviewed.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected source destination changed. Review the backup again.");
        var verified = new List<(FolderImportPlan Plan, string Fingerprint)>();
        if (reviewed.TransferMode == BackupTransferMode.None)
            VerifiedTreeCopy.EnsureDestinationDirectory(reviewed.DestinationPath, ct);
        else if (reviewed.IncludeCurrentFiles && reviewed.CurrentFiles.Fingerprint == "missing")
        {
            if (Directory.Exists(reviewed.OriginalWindowsPath))
                throw new IOException("Files appeared in this Windows folder after review. Review its contents again.");
            Directory.CreateDirectory(reviewed.OriginalWindowsPath);
            verified.Add((reviewed.CurrentFiles, await VerifiedTreeCopy.CopyVerifiedAsync(reviewed.OriginalWindowsPath, reviewed.DestinationPath, ct)));
        }
        else if (reviewed.IncludeCurrentFiles)
            verified.Add((reviewed.CurrentFiles, await FolderImport.ExecuteAsync(reviewed.CurrentFiles, ct)));
        if (reviewed.TransferMode != BackupTransferMode.None && reviewed.AdditionalFiles is { } extra)
        {
            if (!extra.DestinationPath.Equals(reviewed.DestinationPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected source destination changed. Review the backup again.");
            verified.Add((extra, await FolderImport.ExecuteAsync(extra, ct)));
        }
        if (reviewed.TransferMode != BackupTransferMode.None && verified.Count == 0)
            throw new IOException("Choose the source files before changing this Windows folder location.");
        // The verified copy retained the source appearance, including desktop.ini.
        // The source may be a Windows folder redirected onto a network provider.
        await FolderAppearance.EnsureIconAsync(reviewed.CurrentFiles.DestinationPath, FolderAppearance.GetKnownFolderIcon(reviewed.Name), ct);
        foreach (var item in verified) VerifiedTreeCopy.EnsureUnchanged(item.Plan.SourcePath, item.Fingerprint, ct);
        if (!getCurrentPath().Equals(reviewed.OriginalWindowsPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Windows folder changed before backup could finish. Original files and completed copies were retained; review it again.");
        ct.ThrowIfCancellationRequested();
        setPath(reviewed.DestinationPath);
        string? warning = null;
        if (reviewed.TransferMode == BackupTransferMode.Move)
        {
            // Redirection is committed first. An interruption can leave duplicate originals,
            // but never removes the only copy or changes the mapping back to missing files.
            foreach (var item in verified)
            {
                var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(item.Plan.SourcePath,
                    item.Plan.DestinationPath, item.Fingerprint, item.Plan.FileCount, ct);
                if (outcome.RetentionWarning is not null) warning = outcome.RetentionWarning;
            }
        }
        return new(new(reviewed.Name, reviewed.OriginalWindowsPath, reviewed.DestinationPath), warning);
    }

    public static IReadOnlyDictionary<string, Guid> FolderIds { get; } = new Dictionary<string, Guid>
    {
        ["Desktop"] = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        ["Documents"] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        ["Pictures"] = new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        ["Music"] = new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        ["Videos"] = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        ["Downloads"] = new("374DE290-123F-4565-9164-39C4925E467B"),
        ["Favorites"] = new("1777F761-68AD-4D8A-87BD-30B759FA33DD"),
        ["Contacts"] = new("56784854-C6CB-462B-8169-88E350ACB882"),
        ["Saved Games"] = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"),
        ["Links"] = new("BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968"),
        ["Searches"] = new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA"),
        ["3D Objects"] = new("31C0DD25-9439-4F12-BF41-7FF4EDA38722")
    };
    public static string GetPath(string name)
    {
        var id = GetId(name);
        // Resolve the Windows mapping even if a personal folder has not been created yet.
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref id, 0x4000, IntPtr.Zero, out var pointer));
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Windows did not return a folder path."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    public static string GetDefaultPath(string name)
    {
        var id = GetId(name);
        // A provider may have removed the physical default location after redirecting the folder.
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref id, 0x400 | 0x4000, IntPtr.Zero, out var pointer));
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Windows did not return the default folder path."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    public static string? GetRestriction(string name)
    {
        try { return KnownFolderPolicy.GetRestriction(GetId(name)); }
        catch (Exception error) when (error is COMException or IOException)
        { return "This personal folder is unavailable on this Windows installation."; }
    }
    public static async Task<BackupFolder> EnableAsync(string name, string root, CancellationToken ct)
    {
        var source = GetPath(name);
        var destination = PathRules.FullPath(root, name);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This system folder already points at CloudBay, but its original mapping is missing. Restore the backup journal before changing it.");
        KnownFolderPolicy.EnsureRedirectable(GetId(name));
        if (!Path.GetFullPath(source).Equals(GetDefaultPath(name), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{name} is already redirected by Windows, OneDrive, or another provider. Restore it to its local profile location before enabling CloudBay backup.");
        var initialPath = source;
        Directory.CreateDirectory(source);
        var verifiedSource = await VerifiedTreeCopy.CopyVerifiedAsync(source, destination, ct);
        await FolderAppearance.PreserveAfterVerifiedCopyAsync(source, destination, ct);
        await FolderAppearance.EnsureIconAsync(destination, FolderAppearance.GetKnownFolderIcon(name), ct);
        VerifiedTreeCopy.EnsureUnchanged(source, verifiedSource, ct);
        if (!GetPath(name).Equals(initialPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another application changed this system folder during backup. Original files and copies were retained.");
        SetPath(name, destination);
        return new(name, source, destination);
    }
    public static async Task DisableAsync(BackupFolder folder, CancellationToken ct)
        => await ApplyDisableReviewedAsync(PreviewDisable(folder, null, BackupTransferMode.Copy, ct), ct);

    public static BackupRestoreReview PreviewDisable(BackupFolder folder, string? destination = null,
        BackupTransferMode mode = BackupTransferMode.Copy, CancellationToken ct = default)
    {
        ValidateMode(mode);
        ct.ThrowIfCancellationRequested();
        KnownFolderPolicy.EnsureRedirectable(GetId(folder.Name));
        if (!GetPath(folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This folder's Windows location was changed by another app. CloudBay will not overwrite that mapping.");
        return PreviewDisableForPaths(folder, destination, mode, ct);
    }

    internal static BackupRestoreReview PreviewDisableForPaths(BackupFolder folder, string? destination,
        BackupTransferMode mode, CancellationToken ct = default)
    {
        ValidateMode(mode);
        ct.ThrowIfCancellationRequested();
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(destination) ? folder.OriginalPath : destination));
        EnsureSeparatePaths(folder.DestinationPath, destination);
        var files = mode == BackupTransferMode.None ? null : FolderImport.Preview(folder.DestinationPath, destination, ct);
        return new(folder, destination, mode, files);
    }

    public static async Task<BackupTransferOutcome> ApplyDisableReviewedAsync(BackupRestoreReview reviewed, CancellationToken ct)
    {
        ValidateMode(reviewed.TransferMode);
        var folder = reviewed.Folder;
        KnownFolderPolicy.EnsureRedirectable(GetId(folder.Name));
        return await ApplyDisableForPathsAsync(reviewed, () => GetPath(folder.Name), path => SetPath(folder.Name, path), ct);
    }

    internal static async Task<BackupTransferOutcome> ApplyDisableForPathsAsync(BackupRestoreReview reviewed,
        Func<string> getCurrentPath, Action<string> setPath, CancellationToken ct = default)
    {
        ValidateMode(reviewed.TransferMode);
        var folder = reviewed.Folder;
        if (!getCurrentPath().Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This folder's Windows location was changed by another app. CloudBay will not overwrite that mapping.");
        EnsureSeparatePaths(folder.DestinationPath, reviewed.DestinationPath);
        string? verifiedSource = null;
        if (reviewed.TransferMode == BackupTransferMode.None)
            VerifiedTreeCopy.EnsureDestinationDirectory(reviewed.DestinationPath, ct);
        else
        {
            if (reviewed.Files is not { } files || !files.SourcePath.Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase) ||
                !files.DestinationPath.Equals(reviewed.DestinationPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The restore source or destination changed. Review this Windows folder change again.");
            // Reading the files hydrates online-only content before restoring the local folder.
            verifiedSource = await FolderImport.ExecuteAsync(files, ct);
            await FolderAppearance.PreserveAfterVerifiedCopyAsync(folder.DestinationPath, reviewed.DestinationPath, ct);
            VerifiedTreeCopy.EnsureUnchanged(folder.DestinationPath, verifiedSource, ct);
        }
        if (!getCurrentPath().Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another application changed this system folder during restore. Copies were retained.");
        ct.ThrowIfCancellationRequested();
        setPath(reviewed.DestinationPath);
        return reviewed.TransferMode == BackupTransferMode.Move && verifiedSource is not null ?
            await VerifiedTreeMove.RemoveCopiedSourcesAsync(folder.DestinationPath, reviewed.DestinationPath,
                verifiedSource, reviewed.Files!.FileCount, ct) : BackupTransferOutcome.NoRemoval;
    }

    private static void ValidateMode(BackupTransferMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode), "Choose copy, move, or no file transfer.");
    }

    private static void EnsureSeparatePaths(string source, string destination)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose separate Windows and CloudBay folders, with neither inside the other.");
    }
    private static Guid GetId(string name) => FolderIds.TryGetValue(name, out var id) ? id : throw new ArgumentException("Unsupported system folder.", nameof(name));
    private static void SetPath(string name, string path)
    {
        var id = GetId(name);
        Marshal.ThrowExceptionForHR(SHSetKnownFolderPath(ref id, 0, IntPtr.Zero, path));
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHSetKnownFolderPath(ref Guid id, uint flags, IntPtr token, string path);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
