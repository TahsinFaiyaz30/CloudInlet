using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Updates;
using CloudInlet.Views;
using CloudInlet.Windows;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet;

public partial class App
{
    private ClientStateMigration.StateUseLease? _stateLease;
    private MigrationWindow? _migrationWindow;
    private string _clientDataDirectory = BuildInfo.DefaultDataDirectory;
    private string? _migrationInventoryNotice;

    private async Task<bool> PrepareClientStateAsync(bool explicitlyRequested)
    {
        // Store packages require both locations and the routing record to have the same
        // virtualization/retention semantics before relocation can be enabled there.
        if (UpdateInstallation.IsPackaged && !StoreStateRoutingPolicy.SupportsRouting(Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml")))
        {
            if (explicitlyRequested)
            {
                _migrationWindow = new MigrationWindow();
                await _migrationWindow.ShowAsync(new ContentDialog { Title = "A Store update is required",
                    Content = "Install a CloudInlet Store update that supports migration, then review these locations again. Your existing data stays in its current location.",
                    CloseButtonText = "Continue" });
            }
            return true;
        }
        _stateLease = ClientStateMigration.AcquireStateUseLease(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildInfo.Flavor);
        var reviews = new ClientMigrationReview(_stateLease.RecoveryDirectory);
        var pending = reviews.Load();
        var reviewedNow = false;
        var resolution = ClientStateMigration.Resolve(_stateLease);
        if (pending is null && resolution.RequiresRecovery)
            throw new IOException("The migration's saved consent record is missing. Restore that recovery record before opening this profile.");
        if (pending is null)
        {
            _clientDataDirectory = resolution.DataDirectory!;
            // A pending installer may still need its original private cache as a repair
            // source. Finish installer verification before offering any profile relocation.
            try
            {
                WindowsUpdateInstaller.EnsureRecoveryAllowsMigrationReview(
                    Path.Combine(_clientDataDirectory, "Updates"), BuildInfo.Flavor);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or InvalidOperationException)
            {
                _migrationInventoryNotice = "Installation recovery must finish before reviewing other migrations. " + error.Message;
                return true;
            }
            var discovery = ClientMigrationInventory.TryCreate(_clientDataDirectory,
                settings => LegacyMigrationDiscovery.Discover(settings, UpdateInstallation.Load(), _clientDataDirectory));
            if (discovery.Error is not null)
            {
                _migrationInventoryNotice = discovery.Error;
                return true;
            }
            var settings = discovery.Settings!;
            var inventory = discovery.Plan!;
            var plan = MigrationPlan.Create(inventory.Items.Select(item =>
                (item.Id == "application-state" || item.Area is MigrationArea.SyncFolder or MigrationArea.BackupFolder ||
                    ClientRegistrationMigration.IsRegistrationItem(item)) &&
                item.BlockedReason == LegacyMigrationDiscovery.ReviewOnlyReason ? item with { BlockedReason = null } : item));
            if (plan.Items.Count == 0 || !explicitlyRequested && reviews.WasOffered(plan.Fingerprint)) return true;
            _migrationWindow = new MigrationWindow(settings.Theme);
            var dialog = new MigrationDialog(plan);
            await _migrationWindow.ShowAsync(dialog);
            reviews.MarkOffered(plan.Fingerprint);
            if (dialog.ConfirmedSelection is not { CanContinue: true } selected) return true;
            pending = reviews.Create(plan, selected.SelectedIds, dialog.CloudMaintenanceAcknowledged);
            reviewedNow = true;
        }

        _migrationWindow ??= new MigrationWindow();
        var sessionPath = reviews.SessionPath(pending);
        var receipts = reviews.ReceiptDirectory(pending);
        bool IsFolder(string id) => pending.Plan.Items.Single(item => item.Id == id).Area is MigrationArea.SyncFolder or MigrationArea.BackupFolder;
        bool IsRegistration(string id) => ClientRegistrationMigration.IsRegistrationItem(pending.Plan.Items.Single(item => item.Id == id));
        bool IsCloud(string id) => pending.Plan.Items.Single(item => item.Id == id).Area == MigrationArea.CloudPrefix;
        string ResolvedState() => ClientStateMigration.Resolve(_stateLease).DataDirectory ?? throw new IOException("Finish application data recovery first.");
        var operations = pending.SelectedIds.Select(id => new DeferredMigrationOperation(id, pending.Plan.Fingerprint,
            () => CreateOperation(pending, id, sessionPath, receipts),
            () => IsCloud(id) ? ClientCloudPrefixMigration.HasRecovery(pending.Plan, id, receipts) :
                IsRegistration(id) ? ClientRegistrationMigration.HasRecovery(pending.Plan, id, receipts) :
                IsFolder(id) ? ClientFolderMigration.HasRecovery(pending.Plan, id, receipts) :
                id == "application-state" && _stateLease.ReadRoute() is { Location: "Pending" or "Legacy", Plan: { } savedPlan } &&
                    savedPlan.Fingerprint == pending.Plan.Fingerprint,
            ct => IsCloud(id) ? ClientCloudPrefixMigration.AcknowledgeUnstartedAsync(pending.Plan, id, ResolvedState(), receipts, ct) :
                IsRegistration(id) ? ClientRegistrationMigration.AcknowledgeUnstartedAsync(pending.Plan, id, ResolvedState(), receipts, ct) :
                IsFolder(id) ? ClientFolderMigration.AcknowledgeUnstartedAsync(pending.Plan, id, receipts, ct) :
                ClientStateMigration.AcknowledgeUnstartedAsync(_stateLease, pending.Plan, sessionPath, ct),
            () => IsCloud(id) ? ClientCloudPrefixMigration.CanAcknowledgeUnstarted(pending.Plan, id, ResolvedState(), receipts) :
                IsRegistration(id) ? ClientRegistrationMigration.CanAcknowledgeUnstarted(pending.Plan, id, ResolvedState(), receipts) :
                IsFolder(id) ? ClientFolderMigration.CanAcknowledgeUnstarted(pending.Plan, id, receipts) :
                ClientStateMigration.CanAcknowledgeUnstarted(_stateLease, pending.Plan, sessionPath))).ToArray();
        try
        {
            using var session = File.Exists(sessionPath)
                ? MigrationSession.Open(sessionPath, operations)
                : MigrationSession.Create(pending.Plan, pending.SelectedIds, sessionPath, operations);
            if (session.Plan.Fingerprint != pending.Plan.Fingerprint ||
                !session.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(pending.SelectedIds))
                throw new InvalidDataException("The migration session does not match its protected consent. No further changes were started.");
            if (!reviewedNow && session.Items.Any(item => IsCloud(item.Id) && item.State is not
                (MigrationItemState.Completed or MigrationItemState.Reverted or MigrationItemState.OriginalKept or MigrationItemState.Blocked)))
            {
                var acknowledgement = new CheckBox { HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = "Other clients that can change these cloud folders are paused and will stay paused until recovery finishes.",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap } };
                var review = new ContentDialog { Title = "Resume cloud maintenance", Content = acknowledgement,
                    PrimaryButtonText = "Review recovery", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
                    IsPrimaryButtonEnabled = false };
                acknowledgement.Checked += (_, _) => review.IsPrimaryButtonEnabled = true;
                acknowledgement.Unchecked += (_, _) => review.IsPrimaryButtonEnabled = false;
                if (await _migrationWindow.ShowAsync(review) != ContentDialogResult.Primary) return false;
            }
            await MigrationDialogHost.ShowAsync(session, _migrationWindow.ShowAsync, _lifetime.Token);
            // A paused/failed item can own half-committed settings or registrations. Never
            // start the engine until every selected mutation is verified or reverted.
            if (session.Items.Any(item => item.State is not (MigrationItemState.Completed or MigrationItemState.Reverted or MigrationItemState.OriginalKept or MigrationItemState.Blocked)))
                return false;
            await session.VerifyKeptOriginalsAsync(_lifetime.Token);
            reviews.Finish(pending);
        }
        finally { foreach (var operation in operations) operation.Dispose(); }
        resolution = ClientStateMigration.Resolve(_stateLease);
        if (resolution.RequiresRecovery || resolution.DataDirectory is null)
            throw new IOException("Application data still requires recovery. Transfers remain stopped.");
        _clientDataDirectory = resolution.DataDirectory;
        return true;
    }

    private IMigrationOperation CreateOperation(ClientMigrationReview.Consent consent, string id, string sessionPath, string receipts)
    {
        if (consent.Plan.Items.Single(item => item.Id == id).Area == MigrationArea.CloudPrefix)
        {
            var state = ClientStateMigration.Resolve(_stateLease!);
            if (state.RequiresRecovery || state.DataDirectory is null) throw new IOException("Finish application data recovery before changing cloud references.");
            return new ClientCloudPrefixMigration(consent, id, state.DataDirectory, receipts);
        }
        if (ClientRegistrationMigration.IsRegistrationItem(consent.Plan.Items.Single(item => item.Id == id)))
        {
            var state = ClientStateMigration.Resolve(_stateLease!);
            if (state.RequiresRecovery || state.DataDirectory is null) throw new IOException("Finish application data recovery before changing Windows registration.");
            return ClientRegistrationMigration.Open(consent.Plan, id, state.DataDirectory, receipts);
        }
        if (consent.Plan.Items.Single(item => item.Id == id).Area is MigrationArea.SyncFolder or MigrationArea.BackupFolder)
        {
            // Resolve lazily: an earlier selected item may just have relocated state.
            var state = ClientStateMigration.Resolve(_stateLease!);
            if (state.RequiresRecovery || state.DataDirectory is null) throw new IOException("Finish application data recovery before migrating folders.");
            return ClientFolderMigration.HasRecovery(consent.Plan, id, receipts)
                ? ClientFolderMigration.Resume(consent.Plan, id, state.DataDirectory, receipts)
                : ClientFolderMigration.Create(consent.Plan, id, state.DataDirectory, receipts);
        }
        if (id != "application-state") throw new InvalidOperationException("This migration adapter is not available in this build. Your saved locations were retained.");
        var resolution = ClientStateMigration.Resolve(_stateLease!);
        if (resolution.Plan?.Fingerprint == consent.Plan.Fingerprint && resolution.SessionJournalPath == sessionPath)
            return ClientStateMigration.Resume(_stateLease!);
        return ClientStateMigration.Create(_stateLease!, consent.Plan, id, sessionPath);
    }

    private async Task RestartForMigrationAsync()
    {
        if (_exiting) return;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--review-migration");
            start.ArgumentList.Add($"--wait-for-exit={Environment.ProcessId},{process.StartTime.ToUniversalTime().Ticks}");
            using var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("CloudInlet could not restart for migration review.");
            await QuitAsync();
        }
        catch (Exception error) { MainWindow?.ShowUpdateInitializationError(error.Message); }
    }

    private Task<string?> ReviewInstallationMigrationAsync(VerifiedUpdatePackage package)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (MainWindow is null || !MainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var installation = UpdateInstallation.Load();
                if (!installation.CanMigrateInstallation || _controller is null)
                { completion.TrySetResult(null); return; }
                var inventory = LegacyMigrationDiscovery.Discover(_controller.Settings, installation, _clientDataDirectory);
                var item = inventory.Items.SingleOrDefault(value => value.Id == "installation");
                if (item is null || item.BlockedReason != LegacyMigrationDiscovery.ReviewOnlyReason)
                { completion.TrySetResult(null); return; }
                var target = installation.MigrationTarget();
                var registrationOnly = string.Equals(target,
                    CloudInlet.Packaging.InstallationMigrationPolicy.Normalize(installation.Directory), StringComparison.OrdinalIgnoreCase);
                var plan = MigrationPlan.Create([item with { BlockedReason = null }]);
                MainWindow.ShowWindow();
                var dialog = new MigrationDialog(plan,
                    $"CloudInlet {package.Candidate.Version} is ready to install. " + (registrationOnly
                        ? "You can also update this installation's CloudBay registration in its current folder. Choosing Install without migration keeps the current registration and still installs the update."
                        : "You can also move this installation to the reviewed CloudInlet folder and update its installer registration. Choosing Install without migration keeps the existing folder and registration and still installs the update."),
                    "Install without migration") { XamlRoot = ((Microsoft.UI.Xaml.FrameworkElement)MainWindow.Content).XamlRoot };
                await dialog.ShowAsync();
                completion.TrySetResult(dialog.ConfirmedSelection?.SelectedIds.Contains("installation") == true ? target : null);
            }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new IOException("CloudInlet is closing. Reopen it before installing the update."));
        return completion.Task;
    }

}
