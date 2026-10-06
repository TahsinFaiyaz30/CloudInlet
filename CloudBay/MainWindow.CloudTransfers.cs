using CloudBay.Core.Transfers;
using CloudBay.ViewModels;
using CloudBay.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;

namespace CloudBay;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, CloudJobRow> _cloudJobRows = new(StringComparer.Ordinal);
    private IReadOnlyList<TransferJobSnapshot>? _cloudTransferJobsPreview;
    private sealed record CloudJobRow(SettingsCard Card, TextBlock Detail, ProgressBar Progress, TextBlock ProgressDetail, Button Pause, Button Resume, Button Cancel);

    private async void CloudTransfer_Click(object sender, RoutedEventArgs args) => await OpenCloudTransferAsync();
    private async Task<string?> OpenCloudTransferAsync(TransferLocation? source = null, TransferLocation? destination = null)
    {
        if (_closed || _busy || _viewModel.Preview is not null) return null;
        var dialog = await PickCloudTransferAsync(source, destination);
        if (dialog?.Source is null || dialog.Destination is null) return null;
        try
        {
            var id = await _controller.StartCloudTransferAsync(dialog.Source, dialog.Destination, dialog.Operation,
                dialog.Conflicts, dialog.Exclusions, _backupUiLifetime.Token);
            ShowInfo("Cloud transfer started. View Activity to pause, resume, or cancel it.");
            ShowActivity();
            return id;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { if (!_closed) ShowError(error); return null; }
    }

    private async Task<CloudTransferDialog?> PickCloudTransferAsync(TransferLocation? source = null, TransferLocation? destination = null)
    {
        var dialog = new CloudTransferDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), source, destination)
        { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        return await ShowModalAsync(dialog) == ContentDialogResult.Primary && dialog.Source is not null && dialog.Destination is not null ? dialog : null;
    }

    private void RefreshCloudTransferJobs()
    {
        CloudTransferAction.IsEnabled = !_busy && _viewModel.Preview is null;
        BackupCloudTransferCard.IsEnabled = CloudTransferAction.IsEnabled;
        var saved = _viewModel.Preview is null ? _controller.CloudTransferJobs : _cloudTransferJobsPreview ?? [];
        var jobs = saved.Where(job => job.State != TransferJobState.Completed).OrderByDescending(job => job.Plan.CreatedUtc)
            .Concat(saved.Where(job => job.State == TransferJobState.Completed).OrderByDescending(job => job.Plan.CreatedUtc).Take(20)).ToArray();
        var ids = jobs.Select(job => job.Plan.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _cloudJobRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        { CloudTransferJobRows.Children.Remove(_cloudJobRows[id].Card); _cloudJobRows.Remove(id); }
        foreach (var job in jobs)
        {
            if (!_cloudJobRows.TryGetValue(job.Plan.Id, out var row))
            {
                var id = job.Plan.Id;
                var detail = SourceImportDialog.Text("", true);
                var progress = new ProgressBar { Minimum = 0, Maximum = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
                var progressDetail = SourceImportDialog.Text("", true);
                var pause = new Button { Content = "Pause" }; var resume = new Button { Content = "Resume" }; var cancel = new Button { Content = "Cancel" };
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                actions.Children.Add(pause); actions.Children.Add(resume); actions.Children.Add(cancel);
                var content = new StackPanel { Spacing = 8 }; content.Children.Add(detail); content.Children.Add(progress);
                content.Children.Add(progressDetail); content.Children.Add(actions);
                var card = new SettingsCard { HeaderIcon = new FontIcon { Glyph = "\uE753" }, Content = content,
                    ContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                row = new(card, detail, progress, progressDetail, pause, resume, cancel); _cloudJobRows[id] = row;
                pause.Click += async (_, _) => await ControlCloudJobAsync(() => _controller.PauseCloudTransferAsync(id));
                resume.Click += async (_, _) => await ControlCloudJobAsync(() => _controller.ResumeCloudTransferAsync(id, _backupUiLifetime.Token));
                cancel.Click += async (_, _) => await ControlCloudJobAsync(() => _controller.CancelCloudTransferAsync(id));
            }
            var position = Array.IndexOf(jobs, job);
            if (!CloudTransferJobRows.Children.Contains(row.Card)) CloudTransferJobRows.Children.Insert(position, row.Card);
            else if (CloudTransferJobRows.Children.IndexOf(row.Card) != position)
            { CloudTransferJobRows.Children.Remove(row.Card); CloudTransferJobRows.Children.Insert(position, row.Card); }
            row.Card.Header = job.Plan.Source.DisplayName + " → " + job.Plan.Destination.DisplayName + " · " + job.Plan.Operation;
            row.Card.Description = (job.Plan.Source.Path.Length == 0 ? "Root" : job.Plan.Source.Path) + " → " +
                (job.Plan.Destination.Path.Length == 0 ? "Root" : job.Plan.Destination.Path);
            row.Detail.Text = string.Join(" · ", new[]
            {
                job.State.ToString(), job.DiscoveryComplete ? $"{job.CompletedFiles:N0} of {job.FileCount:N0} files verified" :
                    $"{job.CompletedFiles:N0} files verified · {job.FileCount:N0} discovered so far",
                $"{job.QueuedFiles:N0} queued", ClientViewModel.FormatSize(job.TransferredBytes) + " transferred",
                ClientViewModel.FormatSize(job.RemainingBytes) + " remaining",
                job.BytesPerSecond > 0 ? ClientViewModel.FormatSpeed(job.BytesPerSecond) : "",
                job.SkippedFiles > 0 ? $"{job.SkippedFiles:N0} skipped" : "",
                !job.DiscoveryComplete ? "Discovering more files" : "", job.Error ?? ""
            }.Where(value => value.Length > 0));
            var display = ProgressPresentation.ForCloudJob(job);
            row.Progress.Value = display.Value;
            row.Progress.IsIndeterminate = display.IsIndeterminate;
            row.ProgressDetail.Text = display.Label;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Progress, display.Label);
            row.Pause.Visibility = job.State is TransferJobState.Running or TransferJobState.Discovering ? Visibility.Visible : Visibility.Collapsed;
            row.Resume.Visibility = job.State is TransferJobState.Paused or TransferJobState.Attention or TransferJobState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
            row.Cancel.Visibility = job.State is not (TransferJobState.Completed or TransferJobState.Cancelled) ? Visibility.Visible : Visibility.Collapsed;
            row.Pause.IsEnabled = row.Resume.IsEnabled = row.Cancel.IsEnabled = _viewModel.Preview is null;
        }
        CloudTransferJobsPanel.Visibility = jobs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ControlCloudJobAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
    }
}
