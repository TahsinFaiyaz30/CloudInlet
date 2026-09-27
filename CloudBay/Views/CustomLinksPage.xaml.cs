using System;
using System.Linq;
using CloudBay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CloudBay.Views;

public sealed partial class CustomLinksPage : Page
{
    public CustomLinksPage()
    {
        InitializeComponent();
    }

    private async void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow is null) return;
            var picker = new FolderPicker(App.MainWindow.AppWindow.Id)
            {
                CommitButtonText = "Select project"
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is not null && DataContext is ShellViewModel viewModel)
            {
                viewModel.CustomLocalPath = result.Path;
            }
        }
        catch (Exception ex)
        {
            if (DataContext is ShellViewModel viewModel) viewModel.ReportUiError("Folder picker failed", ex);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        if (e.AcceptedOperation != DataPackageOperation.None)
        {
            e.DragUIOverride.Caption = "Use this folder with CloudBay";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var folder = items.OfType<StorageFolder>().FirstOrDefault();
            if (folder is not null && DataContext is ShellViewModel viewModel)
            {
                viewModel.CustomLocalPath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            if (DataContext is ShellViewModel viewModel) viewModel.ReportUiError("Could not read dropped folder", ex);
        }
    }
}
