using CloudInlet.Core.Migration;
using CloudInlet.Core;
using CloudInlet.Application;
using CloudInlet.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using CloudInlet.Packaging;

namespace CloudInlet;

public sealed partial class MainWindow
{
    /// <summary>Isolated UI acceptance only. The adapter moves tiny fixture files below this run's artifacts directory.</summary>
    public async Task RunMigrationUiValidationAsync(string output)
    {
        var theme = Environment.GetCommandLineArgs().Contains("--ui-smoke-theme=Light") ? ElementTheme.Light : ElementTheme.Dark;
        RootGrid.RequestedTheme = theme;
        var preview = ViewModels.ClientPreview.Connected();
        _viewModel.SetPreview(preview with { Settings = preview.Settings with { Theme = theme.ToString() } });
        Refresh();
        await InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(30));
        var suffix = theme == ElementTheme.Light ? "light" : "dark";
        var root = Path.GetFullPath(Path.Combine(output, "migration-fixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var choices = MigrationPlan.Create([
            new("installation", MigrationArea.Installation, "Application installation and registration",
                @"C:\Users\Example\AppData\Local\Programs\CloudBay", @"C:\Users\Example\AppData\Local\Programs\CloudInlet",
                "The installation folder, installer registration and shortcuts move together."),
            new("personal-folder", MigrationArea.BackupFolder, "Personal backup folder",
                @"D:\Personal files\Camera originals and family archive\CloudBay", @"D:\Personal files\Camera originals and family archive\CloudInlet",
                "The folder contents and backup references stay together. Other personal folders remain unchanged."),
            new("cloud-prefix", MigrationArea.CloudPrefix, "B2 backup prefix", "CloudBay/", "CloudInlet/",
                "Review the destination and permissions before copying cloud versions.", "The current key cannot write to the proposed prefix.")
        ]);
        foreach (var width in new[] { 760, 1100 })
        {
            AppWindow.Resize(new SizeInt32(width, 760));
            await Task.Delay(180);
            var review = new MigrationDialog(choices);
            if (review.SelectedIdsForValidation.Count != 0) throw new InvalidOperationException("Migration must be opt-in.");
            review.SelectAllForValidation();
            if (!review.SelectedIdsForValidation.SetEquals(new[] { "installation", "personal-folder" }))
                throw new InvalidOperationException("Select all must exclude unavailable items.");
            review.DeselectAllForValidation();
            if (review.SelectedIdsForValidation.Count != 0) throw new InvalidOperationException("Deselect all did not clear the review.");
            await CaptureMigrationDialogAsync(review, $"migration-choices-{width}-{suffix}");
            review = new MigrationDialog(choices);
            review.PrepareReviewForValidation(["installation", "personal-folder"]);
            if (review.ConfirmedSelection is not null) throw new InvalidOperationException("Opening the review must not approve it.");
            await CaptureMigrationDialogAsync(review, $"migration-review-{width}-{suffix}");
        }
        var cloudReview = new MigrationDialog(MigrationPlan.Create(choices.Items.Where(item => item.Id == "cloud-prefix")
            .Select(item => item with { BlockedReason = null })));
        cloudReview.PrepareReviewForValidation(["cloud-prefix"]);
        if (cloudReview.IsPrimaryButtonEnabled || cloudReview.CloudMaintenanceAcknowledged)
            throw new InvalidOperationException("Cloud maintenance must require a separate acknowledgement.");
        AppWindow.Resize(new SizeInt32(760, 760)); await Task.Delay(150);
        await CaptureMigrationDialogAsync(cloudReview, "migration-cloud-maintenance-760-" + suffix);
        cloudReview.AcknowledgeCloudForValidation();
        if (!cloudReview.IsPrimaryButtonEnabled || !cloudReview.CloudMaintenanceAcknowledged || cloudReview.ConfirmedSelection is not null)
            throw new InvalidOperationException("Acknowledging maintenance must enable confirmation without approving the migration.");

        foreach (var width in new[] { 760, 1100 })
        {
            AppWindow.Resize(new SizeInt32(width, 840)); await Task.Delay(150);
            var run = Path.Combine(root, "keep-original-" + width); Directory.CreateDirectory(run);
            var completed = new MigrationUiFixtureOperation("local", run);
            var cloud = new KeepOriginalUiFixture(run);
            var plan = MigrationPlan.Create([
                new("local", MigrationArea.ApplicationState, "Accounts and saved settings", completed.Source, completed.Target, "Isolated completed fixture"),
                new("cloud", MigrationArea.CloudPrefix, "Cloud folder", "CloudBay/", "CloudInlet/", "Retain partial copies after an unknown response")]);
            completed.ReviewedPlanFingerprint = cloud.ReviewedPlanFingerprint = plan.Fingerprint;
            using var session = MigrationSession.Create(plan, ["local", "cloud"], Path.Combine(run, "session.json"), [completed, cloud]);
            await MigrationDialogHost.ShowAsync(session, async dialog =>
            {
                var progress = (MigrationProgressDialog)dialog;
                dialog.XamlRoot = RootGrid.XamlRoot; dialog.RequestedTheme = theme;
                var showing = ShowModalAsync(dialog);
                try
                {
                    await WaitMigrationAsync(() => progress.IsFinished, "Cloud failure must become visible.");
                    if (!progress.CanKeepOriginalForValidation("cloud") || progress.CanKeepOriginalForValidation("local"))
                        throw new InvalidOperationException("Keep-original eligibility was not limited to the failed cloud item.");
                    progress.ConfirmKeepOriginalForValidation("cloud");
                    if (cloud.KeepCalls != 0) throw new InvalidOperationException("An unreviewed keep-original action ran.");
                    progress.ReviewKeepOriginalForValidation("cloud");
                    await Task.Delay(100);
                    await CaptureProgressAsync(dialog, $"migration-keep-original-confirm-{width}-{suffix}");
                    if (cloud.KeepCalls != 0) throw new InvalidOperationException("Opening the confirmation acted on the cloud item.");
                    progress.ConfirmKeepOriginalForValidation("cloud");
                    await WaitMigrationAsync(() => progress.IsFinished && progress.ItemStatesForValidation["cloud"] == MigrationItemState.OriginalKept,
                        "The retained-original result must be explicit.");
                    if (progress.Completion != MigrationDialogCompletion.Partial || session.IsComplete || cloud.KeepCalls != 1 ||
                        progress.ItemStatesForValidation["local"] != MigrationItemState.Completed || completed.RevertCalls != 0)
                        throw new InvalidOperationException("Keeping the original location changed completed selections or claimed full completion.");
                    await session.VerifyKeptOriginalsAsync();
                    await completed.VerifyAsync(default);
                    await CaptureProgressAsync(dialog, $"migration-original-kept-{width}-{suffix}");
                }
                finally { dialog.Hide(); }
                return await showing;
            });
        }

        foreach (var revert in new[] { false, true })
        {
            AppWindow.Resize(new SizeInt32(900, 840));
            await Task.Delay(180);
            var run = Path.Combine(root, revert ? "revert" : "retry");
            Directory.CreateDirectory(run);
            var first = new MigrationUiFixtureOperation("a-settings", run);
            var second = new MigrationUiFixtureOperation("b-folder", run) { FailOnceAfterMove = true };
            var plan = MigrationPlan.Create([
                new(first.ItemId, MigrationArea.ApplicationState, "Application data", first.Source, first.Target, "Isolated settings fixture."),
                new(second.ItemId, MigrationArea.BackupFolder, "Personal backup folder", second.Source, second.Target, "Isolated backup fixture.")
            ]);
            first.ReviewedPlanFingerprint = second.ReviewedPlanFingerprint = plan.Fingerprint;
            using var session = MigrationSession.Create(plan, [first.ItemId, second.ItemId], Path.Combine(run, "journal.json"), [first, second]);
            await MigrationDialogHost.ShowAsync(session, async dialog =>
            {
                var progress = (MigrationProgressDialog)dialog;
                dialog.XamlRoot = RootGrid.XamlRoot;
                dialog.RequestedTheme = theme;
                var showing = ShowModalAsync(dialog);
                try
                {
                    await WaitMigrationAsync(() => progress.IsFinished, "The injected fixture failure must be visible.");
                    if (progress.Completion != MigrationDialogCompletion.Failed ||
                        progress.ItemStatesForValidation[first.ItemId] != MigrationItemState.Completed ||
                        progress.ItemStatesForValidation[second.ItemId] != MigrationItemState.Failed ||
                        progress.ItemActionsForValidation[first.ItemId] != (false, false))
                        throw new InvalidOperationException("Completed and failed migration items need distinct states and actions.");
                    await CaptureProgressAsync(progress, $"migration-{(revert ? "revert" : "retry")}-failure-{suffix}");
                    if (revert) progress.RequestRevertForValidation(second.ItemId);
                    else progress.RequestRetryForValidation(second.ItemId);
                    await WaitMigrationAsync(() => progress.IsFinished, "Retry or revert did not reach a verified terminal state.");
                    if (revert)
                    {
                        if (session.IsComplete || progress.Completion != MigrationDialogCompletion.Partial ||
                            session.Items.Single(item => item.Id == second.ItemId).State != MigrationItemState.Reverted ||
                            !File.Exists(second.Source) || File.Exists(second.Target))
                            throw new InvalidOperationException("Reverting an incomplete item must not count it as migrated.");
                    }
                    else if (!session.IsComplete || progress.Completion != MigrationDialogCompletion.Verified)
                        throw new InvalidOperationException("Retry must complete only after destination verification.");
                    if (first.ApplyCalls != 1 || first.RevertCalls != 0 || File.Exists(first.Source) || File.ReadAllText(first.Target) != first.Contents)
                        throw new InvalidOperationException("Retry/revert changed an already verified item.");
                    await CaptureProgressAsync(progress, $"migration-{(revert ? "reverted" : "verified")}-{suffix}");
                }
                finally
                {
                    // No forced exit of running migration. Fixtures are bounded; wait for their owned action to finish first.
                    if (progress.IsRunning) await WaitMigrationAsync(() => progress.IsFinished, "Fixture did not stop safely.");
                    progress.Hide();
                }
                return await showing;
            });
        }
        // Stop while the second item has moved but still needs verification. Close/reopen the
        // actual durable session and exercise its UI recovery instead of fabricating status rows.
        var stoppedRun = Path.Combine(root, "stop-reopen");
        Directory.CreateDirectory(stoppedRun);
        var completedItem = new MigrationUiFixtureOperation("a-settings", stoppedRun);
        var interruptedItem = new MigrationUiFixtureOperation("b-folder", stoppedRun) { PauseOnceAfterMove = true };
        var interruptedPlan = MigrationPlan.Create([
            new(completedItem.ItemId, MigrationArea.ApplicationState, "Application data", completedItem.Source, completedItem.Target, "Isolated settings fixture."),
            new(interruptedItem.ItemId, MigrationArea.BackupFolder, "Personal backup folder", interruptedItem.Source, interruptedItem.Target, "Isolated backup fixture.")
        ]);
        completedItem.ReviewedPlanFingerprint = interruptedItem.ReviewedPlanFingerprint = interruptedPlan.Fingerprint;
        var journal = Path.Combine(stoppedRun, "journal.json");
        using (var stopped = MigrationSession.Create(interruptedPlan, [completedItem.ItemId, interruptedItem.ItemId], journal, [completedItem, interruptedItem]))
        {
            await MigrationDialogHost.ShowAsync(stopped, async dialog =>
            {
                var progress = (MigrationProgressDialog)dialog;
                dialog.XamlRoot = RootGrid.XamlRoot;
                dialog.RequestedTheme = theme;
                var showing = ShowModalAsync(dialog);
                try
                {
                    await WaitMigrationAsync(() => progress.IsRunning && progress.ProgressForValidation.Label.Contains("1 of 1 files") &&
                        progress.ItemStatesForValidation[interruptedItem.ItemId] == MigrationItemState.Running,
                        "Live progress did not show the actual completed file move.");
                    if (progress.ProgressForValidation.IsIndeterminate || progress.ProgressForValidation.Value != 100 || stopped.IsComplete)
                        throw new InvalidOperationException("A completed move must not mark its unverified item or session complete.");
                    await CaptureProgressAsync(progress, $"migration-running-{suffix}");
                    progress.RequestStopForValidation();
                    await WaitMigrationAsync(() => progress.IsFinished, "The migration did not stop at a recoverable boundary.");
                    if (progress.Completion != MigrationDialogCompletion.Stopped ||
                        progress.ItemStatesForValidation[interruptedItem.ItemId] != MigrationItemState.Paused ||
                        !progress.ItemActionsForValidation[interruptedItem.ItemId].CanRetry)
                        throw new InvalidOperationException("Safe stop must expose recovery for the interrupted item.");
                    await CaptureProgressAsync(progress, $"migration-paused-{suffix}");
                }
                finally
                {
                    if (progress.IsRunning)
                    {
                        progress.RequestStopForValidation();
                        await WaitMigrationAsync(() => progress.IsFinished, "The isolated fixture did not stop safely.");
                    }
                    progress.Hide();
                }
                return await showing;
            });
        }
        using (var reopened = MigrationSession.Open(journal, [completedItem, interruptedItem]))
        {
            var lastSaved = reopened.History.Last();
            await MigrationDialogHost.ShowAsync(reopened, async dialog =>
            {
                var progress = (MigrationProgressDialog)dialog;
                dialog.XamlRoot = RootGrid.XamlRoot;
                dialog.RequestedTheme = theme;
                var showing = ShowModalAsync(dialog);
                try
                {
                    await WaitMigrationAsync(() => progress.IsFinished, "An interrupted migration needs an explicit recovery choice.");
                    if (!progress.ActivityForValidation.Contains(lastSaved.TimeUtc.ToLocalTime().ToString("g")) ||
                        !progress.ActivityForValidation.Contains(lastSaved.Detail) || interruptedItem.ApplyCalls != 1)
                        throw new InvalidOperationException("Reopening must restore saved activity without silently retrying the interrupted item.");
                    await CaptureProgressAsync(progress, $"migration-reopened-{suffix}");
                    progress.RequestRetryForValidation(interruptedItem.ItemId);
                    await WaitMigrationAsync(() => progress.IsFinished, "The reopened migration did not finish after explicit retry.");
                    if (!reopened.IsComplete || completedItem.ApplyCalls != 1 || completedItem.RevertCalls != 0 ||
                        File.ReadAllText(completedItem.Target) != completedItem.Contents ||
                        File.ReadAllText(interruptedItem.Target) != interruptedItem.Contents)
                        throw new InvalidOperationException("Resume must verify the interrupted item and preserve the completed item.");
                    await CaptureProgressAsync(progress, $"migration-resumed-{suffix}");
                }
                finally
                {
                    if (progress.IsRunning)
                    {
                        progress.RequestStopForValidation();
                        await WaitMigrationAsync(() => progress.IsFinished, "The isolated recovery did not stop safely.");
                    }
                    progress.Hide();
                }
                return await showing;
            });
        }
        var continueRun = Path.Combine(root, "continue-pending");
        Directory.CreateDirectory(continueRun);
        var beforeStop = new MigrationUiFixtureOperation("a-settings", continueRun);
        var afterStop = new MigrationUiFixtureOperation("b-folder", continueRun);
        var continuePlan = MigrationPlan.Create([
            new(beforeStop.ItemId, MigrationArea.ApplicationState, "Application data", beforeStop.Source, beforeStop.Target, "Isolated settings fixture."),
            new(afterStop.ItemId, MigrationArea.BackupFolder, "Personal backup folder", afterStop.Source, afterStop.Target, "Isolated backup fixture.")
        ]);
        beforeStop.ReviewedPlanFingerprint = afterStop.ReviewedPlanFingerprint = continuePlan.Fingerprint;
        using (var pending = MigrationSession.Create(continuePlan, [beforeStop.ItemId, afterStop.ItemId], Path.Combine(continueRun, "session.json"), [beforeStop, afterStop]))
        {
            MigrationProgressDialog? progress = null;
            var requested = 0;
            void StopBetweenItems()
            {
                if (pending.Items.Single(item => item.Id == beforeStop.ItemId).State != MigrationItemState.Completed ||
                    Interlocked.Exchange(ref requested, 1) != 0) return;
                var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(() => { progress!.RequestStopForValidation(); acknowledged.SetResult(); }))
                    throw new InvalidOperationException("The live validation window closed before stopping.");
                // Hold this fixture's completed boundary until the UI issues the real stop request.
                if (!acknowledged.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The UI did not acknowledge a safe stop.");
            }
            pending.Changed += StopBetweenItems;
            try
            {
                await MigrationDialogHost.ShowAsync(pending, async dialog =>
                {
                    progress = (MigrationProgressDialog)dialog;
                    dialog.XamlRoot = RootGrid.XamlRoot;
                    dialog.RequestedTheme = theme;
                    var showing = ShowModalAsync(dialog);
                    try
                    {
                        await WaitMigrationAsync(() => progress.IsFinished, "Stopping between items must leave the remaining work pending.");
                        if (!progress.CanContinueForValidation || afterStop.ApplyCalls != 0 ||
                            pending.Items.Single(item => item.Id == afterStop.ItemId).State != MigrationItemState.Pending)
                            throw new InvalidOperationException("Pending selected work must have a working Continue action after a safe stop.");
                        await CaptureProgressAsync(progress, $"migration-continue-pending-{suffix}");
                        progress.RequestContinueForValidation();
                        await WaitMigrationAsync(() => progress.IsFinished, "Continue did not resume pending work.");
                        if (!pending.IsComplete || beforeStop.ApplyCalls != 1 || afterStop.ApplyCalls != 1)
                            throw new InvalidOperationException("Continue must execute remaining work exactly once and preserve completed items.");
                    }
                    finally
                    {
                        if (progress.IsRunning)
                        {
                            progress.RequestStopForValidation();
                            await WaitMigrationAsync(() => progress.IsFinished, "The continued fixture did not stop safely.");
                        }
                        progress.Hide();
                    }
                    return await showing;
                });
            }
            finally { pending.Changed -= StopBetweenItems; }
        }
        // Exercise the actual startup host and protected state adapter against an
        // isolated profile, rather than treating a generic directory move as acceptance.
        using (var lease = ClientStateMigration.AcquireStateUseLease(Path.Combine(root, "startup-home"), "Release"))
        {
            var folderSource = Path.Combine(root, "startup-folders", BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay");
            var folderTarget = Path.Combine(root, "startup-folders", BuildInfo.ProductName);
            Directory.CreateDirectory(folderSource);
            await File.WriteAllTextAsync(Path.Combine(folderSource, "preserved.txt"), "original fixture bytes");
            var storage = new ClientStorage(lease.LegacyDirectory);
            storage.SaveSettings(new() { RootPath = folderSource, StartAtSignIn = false });
            storage.SaveCredentials(new("fixture-key", "fixture-secret"));
            var plan = MigrationPlan.Create([new("application-state", MigrationArea.ApplicationState, "Application data",
                lease.LegacyDirectory, lease.CanonicalDirectory, "Preserve settings and protected accounts."),
                new("sync-folder", MigrationArea.SyncFolder, "Sync folder", folderSource, folderTarget, "Preserve the original folder and saved references.")]);
            var review = new ClientMigrationReview(lease.RecoveryDirectory);
            var consent = review.Create(plan, ["application-state", "sync-folder"]);
            var startupJournal = review.SessionPath(consent);
            using var operation = new DeferredMigrationOperation("application-state", plan.Fingerprint,
                () => ClientStateMigration.Create(lease, plan, "application-state", startupJournal), () => false,
                ct => ClientStateMigration.AcknowledgeUnstartedAsync(lease, plan, startupJournal, ct),
                () => ClientStateMigration.CanAcknowledgeUnstarted(lease, plan, startupJournal));
            var records = review.ReceiptDirectory(consent);
            using var folder = new DeferredMigrationOperation("sync-folder", plan.Fingerprint,
                () => ClientFolderMigration.Create(plan, "sync-folder", ClientStateMigration.Resolve(lease).DataDirectory!, records),
                () => ClientFolderMigration.HasRecovery(plan, "sync-folder", records),
                ct => ClientFolderMigration.AcknowledgeUnstartedAsync(plan, "sync-folder", records, ct),
                () => ClientFolderMigration.CanAcknowledgeUnstarted(plan, "sync-folder", records));
            using var session = MigrationSession.Create(plan, consent.SelectedIds, startupJournal, [operation, folder]);
            var host = new MigrationWindow(theme.ToString());
            host.AppWindow.Resize(new SizeInt32(760, 760));
            try
            {
                await MigrationDialogHost.ShowAsync(session, async dialog =>
                {
                    var showing = host.ShowAsync(dialog);
                    var progress = (MigrationProgressDialog)dialog;
                    try
                    {
                        await WaitMigrationAsync(() => progress.IsFinished, "The startup profile migration did not finish.");
                        if (!session.IsComplete) throw new IOException(string.Join("\n", session.Items.Select(item => item.Error).Where(error => error is not null)));
                        await Task.Delay(180);
                        await UiSmokeCapture.SaveAsync(dialog, Path.Combine(output, "migration-startup-state-and-folder-760-" + suffix + ".png"));
                        if (new ClientStorage(lease.CanonicalDirectory).LoadCredentials()?.ApplicationKey != "fixture-secret")
                            throw new IOException("The startup migration did not preserve the protected account.");
                        if (new ClientStorage(lease.CanonicalDirectory).LoadSettings().RootPath != folderTarget ||
                            await File.ReadAllTextAsync(Path.Combine(folderTarget, "preserved.txt")) != "original fixture bytes")
                            throw new IOException("The startup migration did not preserve the folder and saved location together.");
                    }
                    finally { progress.Hide(); }
                    return await showing;
                });
                review.Finish(consent);
            }
            finally { host.Finish(); }
        }
        // Exercise the real protected installer journal and read-only WinUI view.
        // This fixture does not run or cancel an installer.
        var installerId = Guid.NewGuid().ToString("N");
        var installerPath = Path.Combine(root, "installer", "host", installerId + ".progress.dpapi");
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
        var requestHash = InstallerProgressJournal.RequestHash("isolated installer UI fixture"u8.ToArray());
        using (var writer = InstallerProgressJournal.Create(installerPath, installerId, requestHash))
        {
            var observer = new InstallerProgressWindow(theme.ToString()); observer.Activate();
            try
            {
                await observer.Ready;
                // RenderTargetBitmap cannot include the desktop Mica backdrop.
                // Flatten only this isolated capture against the theme fallback
                // so translucent native controls and text remain inspectable.
                ((Grid)observer.CaptureContent).Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    theme == ElementTheme.Dark ? global::Windows.UI.Color.FromArgb(255, 32, 32, 32) :
                    global::Windows.UI.Color.FromArgb(255, 243, 243, 243));
                writer.Report("InstallerBusy", "Windows Installer is busy. Waiting before retry 2 of 5.", migrationRequested: true);
                observer.Report(InstallerProgressJournal.Read(installerPath, installerId, requestHash)!);
                await Task.Delay(150);
                await UiSmokeCapture.SaveAsync(observer.CaptureContent, Path.Combine(output, "installer-progress-waiting-" + suffix + ".png"));
                writer.Report("Complete", "Installation folder, registration and installed version passed verification.", "Succeeded", true, true);
                observer.Report(InstallerProgressJournal.Read(installerPath, installerId, requestHash)!);
                await Task.Delay(150);
                await UiSmokeCapture.SaveAsync(observer.CaptureContent, Path.Combine(output, "installer-progress-verified-" + suffix + ".png"));
            }
            finally { observer.Close(); }
        }
        var recovery = new InstallationRecoveryWindow(theme.ToString()); recovery.Activate();
        try
        {
            await recovery.Ready;
            recovery.AppWindow.Resize(new SizeInt32(640, 620));
            ((Grid)recovery.CaptureContent).Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                theme == ElementTheme.Dark ? global::Windows.UI.Color.FromArgb(255, 32, 32, 32) : global::Windows.UI.Color.FromArgb(255, 243, 243, 243));
            const string source = @"C:\Users\Example\AppData\Local\Programs\CloudBay";
            const string target = @"C:\Users\Example\AppData\Local\Programs\CloudInlet";
            recovery.Report("InstallingDestination", "The new installation could not be verified. Retry can continue from the saved step.", source, target, false, true);
            await Task.Delay(150);
            await UiSmokeCapture.SaveAsync(recovery.CaptureContent, Path.Combine(output, "installation-recovery-paused-640-" + suffix + ".png"));
            recovery.Report("Completed", "The installation and its Windows registration passed verification.", source, target, false, false);
            await Task.Delay(150);
            await UiSmokeCapture.SaveAsync(recovery.CaptureContent, Path.Combine(output, "installation-recovery-verified-640-" + suffix + ".png"));
        }
        finally { recovery.Close(); }
        var registrationRecovery = new InstallationRecoveryWindow(theme.ToString()); registrationRecovery.Activate();
        try
        {
            await registrationRecovery.Ready;
            registrationRecovery.AppWindow.Resize(new SizeInt32(640, 620));
            ((Grid)registrationRecovery.CaptureContent).Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                theme == ElementTheme.Dark ? global::Windows.UI.Color.FromArgb(255, 32, 32, 32) : global::Windows.UI.Color.FromArgb(255, 243, 243, 243));
            const string location = @"C:\Applications\My backup app";
            foreach (var phase in new[] { "UpdatingOriginal", "Prepared", "RestoringOriginal", "Completed" })
            {
                var detail = phase switch
                {
                    "UpdatingOriginal" => "The verified application update is being applied in its original folder. Your selected migration and recovery package are already saved.",
                    "Prepared" => "The updated application is verified. Its existing folder will be retained.",
                    "RestoringOriginal" => "The installer needs attention. Retry can restore the updated application in this folder.",
                    _ => "The updated application was restored in its existing folder and its CloudInlet installer registration was verified."
                };
                registrationRecovery.Report(phase, detail, location, location, phase == "UpdatingOriginal", phase != "Completed");
                await Task.Delay(150);
                await UiSmokeCapture.SaveAsync(registrationRecovery.CaptureContent,
                    Path.Combine(output, $"registration-recovery-{phase.ToLowerInvariant()}-640-{suffix}.png"));
            }
            registrationRecovery.Report("UpdatingOriginal", "The installer requested a restart.", location, location, false, true,
                requiresRestart: true,
                restartMessage: "The installer requires a Windows restart to finish its changes. Save your work and restart Windows, then reopen installation recovery and choose Retry. Migration has not been marked complete.");
            await Task.Delay(150);
            await UiSmokeCapture.SaveAsync(registrationRecovery.CaptureContent,
                Path.Combine(output, $"registration-recovery-restart-640-{suffix}.png"));
        }
        finally { registrationRecovery.Close(); }
        await File.WriteAllTextAsync(Path.Combine(output, "migration-assertions-" + suffix + ".txt"),
            "PASS: default-none, select-all available only, deselect-all, separate review, narrow layout, actual fixture move failure, retry verification, failed-item-only revert, completed-item preservation, live measured progress, safe stop, durable reopen, saved activity, explicit resume and Continue pending work.\n" +
            "PASS: startup host, durable consent, real application-state and local folder adapters, account reopening and saved folder references using an isolated profile.\n" +
            "PASS: protected installer progress journal rendered in its standalone read-only WinUI window.\n" +
            "PASS: explicit cloud maintenance acknowledgement; installation, initial update, same-folder registration and restart recovery layouts at 640 pixels. Recovery status captures are view fixtures, not installer acceptance.\n" +
            "PASS: keep-original requires explicit confirmation, preserves completed selections, and reports retained copies without claiming migration success at 760 and 1100 pixels.\n" +
            "This UI run does not exercise installers, registered placeholders or live cloud migration; native placeholder acceptance is a separate desktop test.\n");

        async Task CaptureMigrationDialogAsync(ContentDialog dialog, string name)
        {
            dialog.XamlRoot = RootGrid.XamlRoot;
            dialog.RequestedTheme = theme;
            var showing = ShowModalAsync(dialog);
            try { await Task.Delay(250); await CaptureProgressAsync(dialog, name); }
            finally { dialog.Hide(); await showing; }
        }
        async Task CaptureProgressAsync(ContentDialog dialog, string name)
        {
            await Task.Delay(120);
            dialog.UpdateLayout();
            if (dialog.ActualWidth <= 0 || dialog.ActualHeight <= 0 || dialog.ActualWidth > RootGrid.ActualWidth + 1 || dialog.ActualHeight > RootGrid.ActualHeight + 1)
                throw new InvalidOperationException("The migration dialog must fit the visible window.");
            await UiSmokeCapture.SaveAsync(dialog, Path.Combine(output, name + ".png"));
        }
    }

    private static async Task WaitMigrationAsync(Func<bool> ready, string error)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException(error);
            await Task.Delay(30);
        }
    }

    private sealed class KeepOriginalUiFixture(string root) : IMigrationOperation, IKeepOriginalMigrationOperation
    {
        public string ItemId => "cloud";
        public string ReviewedPlanFingerprint { get; set; } = "";
        public bool CanRevert => true;
        public bool CanKeepOriginal => true;
        public int KeepCalls { get; private set; }
        private string Receipt => Path.Combine(root, "original-kept.txt");
        public Task CheckAsync(CancellationToken token) => Task.CompletedTask;
        public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken token) =>
            Task.FromException(new IOException("The cloud response could not be verified. Original files and partial copies were retained."));
        public Task VerifyAsync(CancellationToken token) => throw new InvalidOperationException("The fixture is incomplete.");
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken token) => throw new IOException("The unknown copy cannot be deleted safely.");
        public Task VerifyRevertedAsync(CancellationToken token) => throw new InvalidOperationException("The fixture was not reverted.");
        public Task KeepOriginalAsync(IProgress<MigrationProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); KeepCalls++;
            using var file = new FileStream(Receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(System.Text.Encoding.UTF8.GetBytes(ReviewedPlanFingerprint)); file.Flush(true);
            progress.Report(new(ItemId, "Original cloud location kept", "Partial copies and recovery records retained. No cloud versions were changed."));
            return Task.CompletedTask;
        }
        public Task VerifyOriginalKeptAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (File.ReadAllText(Receipt) != ReviewedPlanFingerprint) throw new IOException("The isolated keep-original receipt changed.");
            return Task.CompletedTask;
        }
    }

    private sealed class MigrationUiFixtureOperation : IMigrationOperation
    {
        public string ItemId { get; }
        public string ReviewedPlanFingerprint { get; set; } = "";
        public bool CanRevert => true;
        internal string Source { get; }
        internal string Target { get; }
        internal string Contents { get; }
        internal int ApplyCalls { get; private set; }
        internal int RevertCalls { get; private set; }
        internal bool FailOnceAfterMove { get; init; }
        internal bool PauseOnceAfterMove { get; init; }
        internal MigrationUiFixtureOperation(string id, string run)
        {
            ItemId = id; Source = Path.Combine(run, id + "-CloudBay.txt"); Target = Path.Combine(run, id + "-CloudInlet.txt");
            Contents = "CloudInlet migration UI acceptance fixture " + Guid.NewGuid().ToString("N");
            using var output = new StreamWriter(new FileStream(Source, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            output.Write(Contents);
        }
        public Task CheckAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Source) == File.Exists(Target) || File.ReadAllText(File.Exists(Source) ? Source : Target) != Contents)
                throw new IOException("The isolated fixture changed unexpectedly.");
            return Task.CompletedTask;
        }
        public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
        {
            await CheckAsync(ct);
            progress.Report(new(ItemId, "Checking destination", "Checking the isolated destination before moving the file."));
            await Task.Delay(100, ct);
            ApplyCalls++;
            if (File.Exists(Source)) File.Move(Source, Target);
            progress.Report(new(ItemId, "Moving", "The fixture file has moved; destination verification follows.", 1, 1, "files"));
            if (PauseOnceAfterMove && ApplyCalls == 1) await Task.Delay(Timeout.Infinite, ct);
            if (FailOnceAfterMove && ApplyCalls == 1) throw new IOException("Isolated validation: verification was interrupted after the file moved. Retry or revert this change.");
        }
        public Task VerifyAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Source) || File.ReadAllText(Target) != Contents) throw new IOException("Fixture destination verification failed.");
            return Task.CompletedTask;
        }
        public async Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
        {
            await CheckAsync(ct);
            RevertCalls++;
            if (File.Exists(Target)) File.Move(Target, Source);
            progress.Report(new(ItemId, "Reverting failed change", "Only this incomplete fixture was restored.", 1, 1, "files"));
        }
        public Task VerifyRevertedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Target) || File.ReadAllText(Source) != Contents) throw new IOException("Fixture restoration verification failed.");
            return Task.CompletedTask;
        }
    }
}
