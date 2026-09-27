using System;
using CloudBay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace CloudBay.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    public void ResetScroll()
    {
        DashboardScroller.UpdateLayout();
        DashboardScroller.ChangeView(null, 0, null, true);
    }

    private void DashboardScroller_Loaded(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(ResetScroll);
    }

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow is null) return;
            var picker = new FolderPicker(App.MainWindow.AppWindow.Id)
            {
                CommitButtonText = "Use this location"
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is not null && DataContext is ShellViewModel viewModel)
            {
                viewModel.RootPathInput = result.Path;
            }
        }
        catch (Exception ex)
        {
            if (DataContext is ShellViewModel viewModel) viewModel.ReportUiError("Folder picker failed", ex);
        }
    }
}
