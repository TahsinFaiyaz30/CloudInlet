using System;
using System.Globalization;
using System.Linq;
using CloudBay.Application;
using CloudBay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudBay.Views;

public sealed partial class SystemFoldersPage : Page
{
    public SystemFoldersPage()
    {
        InitializeComponent();
    }

    private async void DownloadsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel viewModel)
        {
            await viewModel.SetIncludeDownloadsAsync(DownloadsToggle.IsOn);
        }
    }

    private async void RecoverLegacy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not KnownFolderItemViewModel folder ||
            DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        try
        {
            var preview = await viewModel.PreviewUnmanagedRestoreAsync(folder);
            var dialog = BuildRecoveryDialog($"Recover {folder.DisplayName} to the local profile?",
                preview, "Recover local");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await viewModel.RecoverUnmanagedFolderAsync(folder);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiError("Recovery preview failed", ex);
        }
    }

    private async void RecoverOrphan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OrphanedFolderItemViewModel orphan, Tag: string destinationChoice } ||
            DataContext is not ShellViewModel viewModel || viewModel.IsBusy)
        {
            return;
        }

        try
        {
            var preview = await viewModel.PreviewOrphanRecoveryAsync(orphan, destinationChoice);
            var target = destinationChoice == "Cloud" ? "cloud root" : "local profile";
            var dialog = BuildRecoveryDialog($"Recover {orphan.Kind} files to the {target}?",
                preview, "Copy files");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await viewModel.RecoverOrphanAsync(orphan, destinationChoice);
                await viewModel.RefreshOrphansAsync();
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiError("OneDrive recovery preview failed", ex);
        }
    }

    private ContentDialog BuildRecoveryDialog(string title, FolderRecoveryPreview preview, string actionLabel)
    {
        var findings = preview.Issues.Count == 0
            ? "No path collisions or permission blocks were found."
            : string.Join("\n", preview.Issues.Select(issue => "• " + issue));
        var size = preview.TotalBytes.ToString("N0", CultureInfo.CurrentCulture);
        var files = preview.TotalFiles.ToString("N0", CultureInfo.CurrentCulture);
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"Source: {preview.SourcePath}",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Destination: {preview.DestinationPath}",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{files} files · {size} bytes. The source files are preserved. CloudBay records the operation before copying.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = findings,
            TextWrapping = TextWrapping.WrapWholeWords
        });

        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = actionLabel,
            IsPrimaryButtonEnabled = preview.CanProceed,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
    }
}
