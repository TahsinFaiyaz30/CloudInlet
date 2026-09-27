using Microsoft.Win32;

namespace CloudBay.Core.Folders;

public enum UserShellFolderDiagnosticState
{
    Missing,
    MatchesShell,
    Mismatch,
    Invalid,
    Unverified
}

public sealed record UserShellFolderDiagnostic(
    KnownFolderKind Kind,
    string ValueName,
    string? RawPath,
    string? ExpandedPath,
    UserShellFolderDiagnosticState State,
    string Message);

/// <summary>Read-only inspection of Explorer's per-user settings; shell APIs remain authoritative.</summary>
internal static class UserShellFolderRegistry
{
    private const string Subkey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    internal static UserShellFolderDiagnostic Read(KnownFolderKind kind, string shellPath)
    {
        string name = kind switch
        {
            KnownFolderKind.Desktop => "Desktop",
            KnownFolderKind.Documents => "Personal",
            KnownFolderKind.Pictures => "My Pictures",
            KnownFolderKind.Music => "My Music",
            KnownFolderKind.Videos => "My Video",
            KnownFolderKind.Downloads => NativeKnownFolders.FolderId(kind).ToString("B").ToUpperInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Subkey, writable: false);
            string? raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(raw))
                return new(kind, name, raw, null, UserShellFolderDiagnosticState.Missing,
                    "Explorer's User Shell Folders value is absent; the Windows shell path is authoritative.");
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            if (!Path.IsPathFullyQualified(expanded))
                return new(kind, name, raw, expanded, UserShellFolderDiagnosticState.Invalid,
                    "Explorer's User Shell Folders value does not resolve to an absolute path.");
            if (string.IsNullOrWhiteSpace(shellPath))
                return new(kind, name, raw, expanded, UserShellFolderDiagnosticState.Unverified,
                    $"Explorer's User Shell Folders value resolves to {expanded}; the Windows shell path is unavailable.");
            if (FolderTransfer.PathEquals(expanded, shellPath))
                return new(kind, name, raw, expanded, UserShellFolderDiagnosticState.MatchesShell,
                    "Explorer's User Shell Folders value matches the Windows shell path.");
            return new(kind, name, raw, expanded, UserShellFolderDiagnosticState.Mismatch,
                $"Explorer's User Shell Folders value resolves to {expanded}, while Windows reports {shellPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return new(kind, name, null, null, UserShellFolderDiagnosticState.Invalid,
                $"Explorer's User Shell Folders value could not be checked: {ex.Message}");
        }
    }
}
