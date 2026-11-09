using CloudInlet.Core.Sync;

namespace CloudInlet.Tests;

/// <summary>Read-only validation of a freshly provisioned disposable account's folders.</summary>
internal static class GuestKnownFolderPreflight
{
    public static void Validate(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        var inspected = 0;
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A selected Windows folder or ordinary subfolder is linked: " + directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++inspected > 1_000) throw new IOException("The disposable account's folder inventory is unexpectedly large.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Use the production ACL/tag-aware classification; never follow a link.
                    if (VerifiedTreeCopy.IsProtectedCompatibilityJunction(entry)) continue;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("An unexpected linked directory exists in the disposable account: " + entry);
                    pending.Push(entry); // Empty native subfolders need not be removed for this test.
                }
                else
                {
                    const FileAttributes nativeIni = FileAttributes.Hidden | FileAttributes.System;
                    if (!Path.GetFileName(entry).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                        (attributes & nativeIni) != nativeIni || (attributes & FileAttributes.ReparsePoint) != 0 ||
                        new FileInfo(entry).Length > 64 * 1024)
                        throw new IOException("Unexpected existing content in the disposable account; it was retained: " + entry);
                }
            }
        }
    }
}
