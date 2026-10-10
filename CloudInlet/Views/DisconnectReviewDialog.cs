using CloudInlet.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

internal sealed class DisconnectReviewDialog : ContentDialog
{
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly Dictionary<DisconnectMode, RadioButton> _choices = [];
    internal DisconnectMode Mode => _choices.Single(pair => pair.Value.IsChecked == true).Key;
    internal FrameworkElement CaptureContent => _body;

    internal DisconnectReviewDialog()
    {
        Title = "Disconnect this B2 account?"; CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        SourceImportDialog.ConfigureLayout(this);
        _body.Children.Add(SourceImportDialog.Text("Choose what happens to the local CloudInlet copy. Your Backblaze B2 files and versions are kept in every option.", true));
        var group = Guid.NewGuid().ToString("N");
        Add(DisconnectMode.DownloadAndDisconnect, "Download files, then disconnect",
            "Download online-only files and restore Windows folders to their previous locations. All local files are kept. This may take time and use disk space.");
        Add(DisconnectMode.DisconnectOnly, "Disconnect only",
            "Keep downloaded files and online-only placeholders without downloading or removing anything. Reconnect to open online-only files. Windows folders return to their previous locations without moving files.");
        Add(DisconnectMode.RemoveLocalCopyAndDisconnect, "Remove local cloud copies, then disconnect",
            "Remove only unchanged local files and online-only placeholders backed by a verified B2 version. Keep unsynced files, changed files, personal folders, and unverified copies. Nothing is downloaded.");
        _body.Children.Add(SourceImportDialog.Text("Active transfers are paused with recoverable checkpoints. Keep CloudInlet running until disconnect completes.", true));
        _choices[DisconnectMode.DisconnectOnly].IsChecked = true;
        Content = SourceImportDialog.ScrollContent(_body);

        void Add(DisconnectMode mode, string name, string detail)
        {
            var content = new StackPanel { Spacing = 4 }; content.Children.Add(SourceImportDialog.Heading(name)); content.Children.Add(SourceImportDialog.Text(detail, true));
            var choice = new RadioButton { GroupName = group, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 48 };
            choice.Checked += (_, _) => PrimaryButtonText = mode switch
            {
                DisconnectMode.DownloadAndDisconnect => "Download and disconnect",
                DisconnectMode.RemoveLocalCopyAndDisconnect => "Remove copies and disconnect",
                _ => "Disconnect only"
            };
            _choices.Add(mode, choice); _body.Children.Add(SourceImportDialog.Surface(choice));
        }
    }
}
