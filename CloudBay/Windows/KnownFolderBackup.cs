using System.Runtime.InteropServices;
using CloudBay.Core;
using CloudBay.Core.Sync;

namespace CloudBay.Windows;

public sealed record BackupSourceReview(string Name, string OriginalWindowsPath,
    FolderImportPlan CurrentFiles, FolderImportPlan? AdditionalFiles, bool IsRedirected);

/// <summary>Opt-in Known Folder backup. Original data remains until the user removes it.</summary>
public static class KnownFolderBackup
{
    public static BackupSourceReview Preview(string name, string root, string? additionalSource = null, CancellationToken ct = default)
    {
        KnownFolderPolicy.EnsureRedirectable(GetId(name));
        var original = GetPath(name);
        var destination = PathRules.FullPath(root, name);
        if (Path.GetFullPath(original).Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This Windows folder already points at CloudBay. Recover its backup record before changing its location.");
        var current = Directory.Exists(original) ? FolderImport.Preview(original, destination, ct) :
            new FolderImportPlan(original, destination, "missing", 0, 0, null, false);
        var extra = string.IsNullOrWhiteSpace(additionalSource) || Path.GetFullPath(additionalSource).Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase)
            ? null : FolderImport.Preview(additionalSource, destination, ct);
        if (extra is not null && current.AvailableBytes is { } available && available < checked(current.TotalBytes + extra.TotalBytes))
            throw new IOException("The destination drive does not have enough space for both selected sources.");
        return new(name, original, current, extra,
            !Path.GetFullPath(original).Equals(GetDefaultPath(name), StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<BackupFolder> EnableReviewedAsync(BackupSourceReview reviewed, CancellationToken ct)
    {
        KnownFolderPolicy.EnsureRedirectable(GetId(reviewed.Name));
        if (!GetPath(reviewed.Name).Equals(reviewed.OriginalWindowsPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another app changed this Windows folder location. Review the source again; the mapping was retained.");
        string verifiedOriginal;
        if (reviewed.CurrentFiles.Fingerprint == "missing")
        {
            if (Directory.Exists(reviewed.OriginalWindowsPath))
                throw new IOException("Files appeared in this Windows folder after review. Review its contents again.");
            Directory.CreateDirectory(reviewed.OriginalWindowsPath);
            verifiedOriginal = await VerifiedTreeCopy.CopyVerifiedAsync(reviewed.OriginalWindowsPath, reviewed.CurrentFiles.DestinationPath, ct);
        }
        else verifiedOriginal = await FolderImport.ExecuteAsync(reviewed.CurrentFiles, ct);
        // A second selected source is copied without abandoning current Windows files.
        if (reviewed.AdditionalFiles is { } extra) await FolderImport.ExecuteAsync(extra, ct);
        // The verified copy retained the source appearance, including desktop.ini.
        // The source may be a Windows folder redirected onto a network provider.
        await FolderAppearance.EnsureIconAsync(reviewed.CurrentFiles.DestinationPath, FolderAppearance.GetKnownFolderIcon(reviewed.Name), ct);
        VerifiedTreeCopy.EnsureUnchanged(reviewed.OriginalWindowsPath, verifiedOriginal, ct);
        if (!GetPath(reviewed.Name).Equals(reviewed.OriginalWindowsPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Windows folder changed before backup could finish. Original files and completed copies were retained; review it again.");
        SetPath(reviewed.Name, reviewed.CurrentFiles.DestinationPath);
        return new(reviewed.Name, reviewed.OriginalWindowsPath, reviewed.CurrentFiles.DestinationPath);
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
        await VerifiedTreeCopy.CopyAsync(source, destination, ct);
        await FolderAppearance.PreserveAsync(source, destination, ct);
        await FolderAppearance.EnsureIconAsync(destination, FolderAppearance.GetKnownFolderIcon(name), ct);
        if (!GetPath(name).Equals(initialPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another application changed this system folder during backup. Original files and copies were retained.");
        SetPath(name, destination);
        return new(name, source, destination);
    }
    public static async Task DisableAsync(BackupFolder folder, CancellationToken ct)
    {
        KnownFolderPolicy.EnsureRedirectable(GetId(folder.Name));
        if (!GetPath(folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This folder's Windows location was changed by another app. CloudBay will not overwrite that mapping.");
        // Reading the files hydrates online-only content before restoring the local folder.
        await VerifiedTreeCopy.CopyAsync(folder.DestinationPath, folder.OriginalPath, ct);
        await FolderAppearance.PreserveAsync(folder.DestinationPath, folder.OriginalPath, ct);
        if (!GetPath(folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another application changed this system folder during restore. Copies were retained.");
        SetPath(folder.Name, folder.OriginalPath);
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
