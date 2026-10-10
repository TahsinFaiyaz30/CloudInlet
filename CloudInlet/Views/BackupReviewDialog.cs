using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

/// <summary>Shows the exact reviewed transfer and Windows location before applying either.</summary>
internal sealed class BackupReviewDialog : ContentDialog
{
    internal bool AddSourceRequested => false;
    internal bool RemoveSourceRequested => false;
    internal FrameworkElement CaptureContent { get; }

    internal BackupReviewDialog(BackupSourceReview review)
    {
        Title = "Review " + review.Name + " backup";
        PrimaryButtonText = review.TransferMode == BackupTransferMode.None ? "Turn on without importing" :
            review.TransferMode == BackupTransferMode.Move ? "Move files and turn on" : "Copy files and turn on";
        var body = Begin(); CaptureContent = body;
        if (review.IncludeCurrentFiles && review.TransferMode != BackupTransferMode.None)
            AddPlan(body, "Source · Current Windows folder", review.CurrentFiles);
        if (review.AdditionalFiles is { } extra) AddPlan(body, "Source · Chosen folder", extra);
        body.Children.Add(SourceImportDialog.PathCard("Windows will open", "CloudInlet · " + review.Name, review.DestinationPath));
        if (review.TransferMode == BackupTransferMode.None)
            body.Children.Add(SourceImportDialog.Text("No existing files are imported. Files in your previous Windows location stay there. Files already in this CloudInlet folder remain available.", true));
        else
        {
            var online = review.CurrentFiles.HasOnlineOnlyFiles && review.IncludeCurrentFiles || review.AdditionalFiles?.HasOnlineOnlyFiles == true;
            if (online) body.Children.Add(Notice("Some files are online only. Keep the source cloud app signed in and running until verification finishes."));
            body.Children.Add(SourceImportDialog.Text("Copies are verified before Windows changes its default location. Differing destination files are retained as separate copies.", true));
            if (review.TransferMode == BackupTransferMode.Move)
                body.Children.Add(Notice("Move removes verified originals after Windows changes its location. If the source belongs to another cloud app, those removals can delete its cloud files too. Changed or blocked originals are retained and reported."));
            else body.Children.Add(SourceImportDialog.Text("Original files stay in the chosen source. CloudInlet does not import other Windows or cloud folders automatically.", true));
        }
        Finish(body);
    }

    internal BackupReviewDialog(BackupRestoreReview review, bool freeLocalSpace = false)
    {
        Title = "Review stopping " + review.Folder.Name + " backup";
        PrimaryButtonText = review.TransferMode == BackupTransferMode.None ? "Turn off without restoring" :
            review.TransferMode == BackupTransferMode.Move ? "Move files and turn off" : "Copy files and turn off";
        var body = Begin(); CaptureContent = body;
        if (review.Files is { } files) AddPlan(body, "Source · CloudInlet", files);
        else body.Children.Add(SourceImportDialog.PathCard("Files stay in", "CloudInlet · " + review.Folder.Name, review.Folder.DestinationPath));
        body.Children.Add(SourceImportDialog.PathCard("Windows will open", review.Folder.Name, review.DestinationPath));
        body.Children.Add(SourceImportDialog.Text(review.TransferMode == BackupTransferMode.None
            ? "No files are restored, moved or deleted. Files already at the selected Windows location remain there. Your existing CloudInlet files stay available."
            : "CloudInlet downloads online-only source files as needed, copies them, and verifies them before changing the Windows location. Differing destination files are retained.", true));
        if (review.TransferMode == BackupTransferMode.Move)
            body.Children.Add(Notice("Move removes verified originals from CloudInlet. These removals also remove current B2 copies through sync; retained versions follow your bucket's version policy. Large batches can require a deletion review. Changed or blocked originals remain in CloudInlet."));
        else if (review.TransferMode == BackupTransferMode.Copy)
            body.Children.Add(SourceImportDialog.Text("CloudInlet and B2 copies are retained. If the destination is another cloud's Windows folder, that app handles its uploads.", true));
        if (freeLocalSpace)
            body.Children.Add(Notice("After backup is off, eligible downloaded CloudInlet copies become online only. Files still waiting to upload remain on this PC. Your B2 files are kept."));
        Finish(body);
    }

    private StackPanel Begin()
    {
        CloseButtonText = "Cancel"; DefaultButton = ContentDialogButton.Close;
        SourceImportDialog.ConfigureLayout(this);
        return new() { Spacing = 16 };
    }
    private void Finish(StackPanel body) => Content = SourceImportDialog.ScrollContent(body);
    private static InfoBar Notice(string message) => new() { IsOpen = true, IsClosable = false,
        Severity = InfoBarSeverity.Warning, Message = message };
    private static void AddPlan(StackPanel body, string title, FolderImportPlan plan)
    {
        body.Children.Add(SourceImportDialog.PathCard(title, Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourcePath)), plan.SourcePath));
        body.Children.Add(SourceImportDialog.Summary(plan.FileCount, plan.TotalBytes, plan.AvailableBytes));
    }
}
