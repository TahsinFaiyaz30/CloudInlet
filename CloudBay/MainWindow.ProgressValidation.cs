using CloudBay.Core.Transfers;
using CloudBay.ViewModels;
using CloudBay.Views;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace CloudBay;

public sealed partial class MainWindow
{
    private async Task RunCloudJobProgressUiValidationAsync(string output, ElementTheme theme, string suffix)
    {
        var prior = _viewModel.Preview;
        var plan = new TransferJobPlan("progress-fixture", new("b2", "fixture", "bucket", "", "", "Backblaze B2"),
            new("onedrive", "fixture", "drive", "", "", "OneDrive"), TransferOperation.Copy, TransferConflictPolicy.Skip, [], DateTimeOffset.UtcNow);
        var measured = new TransferJobSnapshot(plan, TransferJobState.Running, true, 4, 1, 1, 2097152, 1048576, 1048576, 2, 0, []);
        try
        {
            _viewModel.SetPreview(ClientPreview.Connected() with { Settings = ClientPreview.Connected().Settings with { Theme = theme.ToString() } });
            RequestNavigationRoute("activity");
            foreach (var width in new[] { 800, 1300 })
            {
                AppWindow.Resize(new SizeInt32(width, 840));
                foreach (var fixture in new[]
                {
                    (Name: "known", Job: measured),
                    (Name: "discovering", Job: measured with { DiscoveryComplete = false, State = TransferJobState.Discovering }),
                    (Name: "zero-byte", Job: measured with { TotalBytes = 0, TransferredBytes = 0, RemainingBytes = 0 })
                })
                {
                    _cloudTransferJobsPreview = [fixture.Job];
                    Refresh();
                    await Task.Delay(180); RootGrid.UpdateLayout();
                    var row = _cloudJobRows[plan.Id];
                    if (row.ProgressDetail is not { ActualHeight: > 0, Visibility: Visibility.Visible } ||
                        row.ProgressDetail.Text != ProgressPresentation.ForCloudJob(fixture.Job).Label ||
                        row.Progress.IsIndeterminate != !fixture.Job.DiscoveryComplete ||
                        row.ProgressDetail.ActualWidth > CloudTransferJobsPanel.ActualWidth)
                        throw new InvalidOperationException("Cloud job progress must show truthful percentage and sizes/counts, stay indeterminate during discovery, and fit the Activity panel.");
                    await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"cloud-job-progress-{fixture.Name}-{width}{suffix}.png"));
                }
            }
            _cloudTransferJobsPreview = null;
            var discovery = ClientPreview.Connected();
            _viewModel.SetPreview(discovery with
            {
                Settings = discovery.Settings with { Theme = theme.ToString() },
                Snapshot = discovery.Snapshot with
                {
                    State = CloudBay.Core.ClientState.Syncing, Message = "Discovering cloud files", TransferTotalKnown = false,
                    TransferredBytes = 1048576, TransferTotalBytes = 2097152
                }
            });
            AppWindow.Resize(new SizeInt32(800, 840)); RequestNavigationRoute("overview"); Refresh();
            await Task.Delay(180); RootGrid.UpdateLayout();
            if (!TransferProgress.IsIndeterminate || TransferProgressDetail is not { ActualHeight: > 0, Visibility: Visibility.Visible } ||
                TransferProgressDetail.Text != "1 MiB transferred · 2 MiB discovered so far")
                throw new InvalidOperationException("Overview aggregate discovery must show measured bytes without a misleading completion percentage.");
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"overview-discovery-progress-800{suffix}.png"));
            await File.AppendAllTextAsync(Path.Combine(output, "transfers-validation.txt"),
                $"PASS: {theme} cloud jobs visibly show percentage plus processed/total bytes or zero-byte file counts at 800 and 1300px; incomplete discovery and overview aggregate discovery show only measured bytes and discovered inventory.{Environment.NewLine}");
        }
        finally
        {
            _cloudTransferJobsPreview = null;
            _viewModel.SetPreview(prior);
            Refresh();
        }
    }
}
