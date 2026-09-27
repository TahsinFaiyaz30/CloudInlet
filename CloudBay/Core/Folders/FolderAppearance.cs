using System.Text;

namespace CloudBay.Core.Folders;

internal static class FolderAppearance
{
    internal sealed record Result(bool CreatedDesktopIni, FileAttributes OriginalFolderAttributes, string? Warning);

    internal static Result Apply(KnownFolderKind kind, string destination)
    {
        string desktopIniPath = Path.Combine(destination, "desktop.ini");
        FileAttributes originalAttributes = File.GetAttributes(destination);
        bool created = false;
        string? warning = null;
        if (!File.Exists(desktopIniPath))
        {
            var definition = NativeKnownFolders.GetDefinition(kind);
            string content = CreateDesktopIni(definition);
            if (content.Length == 0)
                throw new InvalidOperationException($"Windows did not provide shell appearance data for {kind}.");
            using (var stream = new FileStream(desktopIniPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Encoding.Unicode, bufferSize: 4096, leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            created = true;
            File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);
        }
        else
        {
            warning = "Existing desktop.ini was preserved; its folder appearance may differ from Windows defaults.";
        }

        // Explorer reads desktop.ini only for folders marked System or ReadOnly.
        File.SetAttributes(destination, originalAttributes | FileAttributes.ReadOnly);
        return new Result(created, originalAttributes, warning);
    }

    private static string CreateDesktopIni(NativeKnownFolders.ShellFolderDefinition definition)
    {
        var shell = new StringBuilder();
        if (SafeValue(definition.LocalizedName) is { } name)
            shell.Append("LocalizedResourceName=").AppendLine(name);
        if (SafeValue(definition.Tooltip) is { } tooltip)
            shell.Append("InfoTip=").AppendLine(tooltip);
        if (SafeValue(definition.Icon) is { } icon)
            shell.Append("IconResource=").AppendLine(icon);

        if (shell.Length == 0 && definition.FolderType == Guid.Empty) return string.Empty;
        var output = new StringBuilder("[.ShellClassInfo]\r\n");
        output.Append(shell);
        if (definition.FolderType != Guid.Empty)
            output.Append("[ViewState]\r\nFolderType=")
                .Append(definition.FolderType.ToString("B").ToUpperInvariant()).Append("\r\n");
        return output.ToString();
    }

    private static string? SafeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n', '\0']) >= 0
            ? null : value;
}
