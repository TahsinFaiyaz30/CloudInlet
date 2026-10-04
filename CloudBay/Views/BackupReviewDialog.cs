using CloudBay.Windows;
using CloudBay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudBay.Views;

/// <summary>Reviews the actual Windows mapping before copying or redirecting a known folder.</summary>
internal sealed class BackupReviewDialog : ContentDialog
{
    internal bool AddSourceRequested { get; private set; }
    internal bool RemoveSourceRequested { get; private set; }
    internal FrameworkElement CaptureContent { get; }

    internal BackupReviewDialog(BackupSourceReview review)
    {
        Title = "Back up " + review.Name;
        PrimaryButtonText = "Back up " + review.Name;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 640d;
        var body = new StackPanel { Spacing = 16 };
        CaptureContent = body;
        body.Children.Add(SourceImportDialog.PathCard("Current Windows location", review.Name, review.OriginalWindowsPath));
        body.Children.Add(SourceImportDialog.Text($"{review.CurrentFiles.FileCount:N0} {(review.CurrentFiles.FileCount == 1 ? "file" : "files")} · " +
            ClientViewModel.FormatSize(review.CurrentFiles.TotalBytes) + (review.CurrentFiles.AvailableBytes is { } free
                ? " · " + ClientViewModel.FormatSize(free) + " available on the destination drive" : ""), true));
        if (review.AdditionalFiles is { } extra)
        {
            body.Children.Add(SourceImportDialog.PathCard("Additional copy source", Path.GetFileName(extra.SourcePath), extra.SourcePath));
            body.Children.Add(SourceImportDialog.Text($"{extra.FileCount:N0} additional {(extra.FileCount == 1 ? "file" : "files")} · " + ClientViewModel.FormatSize(extra.TotalBytes), true));
        }
        body.Children.Add(SourceImportDialog.PathCard("New Windows location", "CloudBay · " + review.Name, review.CurrentFiles.DestinationPath));
        var online = review.CurrentFiles.HasOnlineOnlyFiles || review.AdditionalFiles?.HasOnlineOnlyFiles == true;
        if (review.IsRedirected || online)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Message = (review.IsRedirected ? "Windows currently uses a redirected folder. Its original source folder and cloud app connection are retained." : "") +
                    (review.IsRedirected && online ? " " : "") +
                    (online ? "Some files are online only; keep the source app signed in and running until the copies finish." : "") });
        body.Children.Add(SourceImportDialog.Text("CloudBay copies and verifies your files, then becomes this Windows folder's default location. Original files and differing destination files are retained. Turning backup off downloads its files and restores the reviewed original Windows location.", true));
        var choose = new Button { Content = review.AdditionalFiles is null ? "Add files from another folder…" : "Remove additional source", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += (_, _) =>
        {
            if (review.AdditionalFiles is null) AddSourceRequested = true;
            else RemoveSourceRequested = true;
            Hide();
        };
        body.Children.Add(choose);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 540, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
}
