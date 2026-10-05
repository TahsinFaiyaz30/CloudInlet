using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudBay.Core.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class UpdateCoordinatorTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified installer fixture");

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OldNotificationCannotDownloadOrInstallANewerVersionAfterWaitingForACheck(bool install)
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync();
        if (install) await fixture.Updater.DownloadAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Responder = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.0"), UpdateManifestRules.JsonOptions)) };
        };
        var check = fixture.Updater.CheckAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var action = install ? fixture.Updater.InstallVersionAsync("1.1.0") : fixture.Updater.DownloadVersionAsync("1.1.0");
        Assert.IsFalse(action.IsCompleted);
        release.TrySetResult();
        await check;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => action);
        Assert.AreEqual("1.2.0", fixture.Updater.Snapshot.Candidate!.Version);
        Assert.AreEqual(0, fixture.Installer.Calls);
        Assert.AreEqual(install ? 3 : 2, fixture.Requests.Count, "Only the feed check may run; the stale button must not transfer a different installer.");
    }

    [TestMethod]
    public async Task RepairedPreferencesStayValidAndRetainVerifiedDownloadAcrossRestart()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Cache, "preferences.json"), "{interrupted");
        await fixture.RestartAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        await fixture.Updater.CheckAsync();
        await fixture.Updater.DownloadAsync();
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        await fixture.RestartAsync();
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        Assert.IsTrue(fixture.Updater.Preferences.AutomaticChecks);
        Assert.IsFalse(fixture.Updater.Preferences.AutomaticallyInstall);
    }

    [TestMethod]
    public async Task InstalledVariantSelectsOnlyMatchingDebugMsiWithoutCrossingChannels()
    {
        var identity = new InstalledUpdateIdentity("1.0.0", UpdateBuildFlavor.Debug, UpdateInstallerKind.Msi);
        await using var fixture = new Fixture(identity);
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(UpdateBuildFlavor.Debug, fixture.Updater.Snapshot.Candidate!.Asset.BuildFlavor);
        Assert.AreEqual(UpdateInstallerKind.Msi, fixture.Updater.Snapshot.Candidate.Asset.InstallerKind);
        await fixture.Updater.DownloadAsync();
        Assert.IsTrue(fixture.Requests.Last().EndsWith("CloudBay-1.1.0-win-x64-debug-setup.msi", StringComparison.Ordinal));
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task EqualAndOlderVersionsNeverDownloadOrOfferReinstallation()
    {
        foreach (var version in new[] { "1.0.0", "0.9.0" })
        {
            await using var fixture = new Fixture(); fixture.Manifest = Manifest(version);
            await fixture.Updater.CheckAsync();
            Assert.AreEqual(UpdateState.UpToDate, fixture.Updater.Snapshot.State);
            Assert.IsNull(fixture.Updater.Snapshot.Candidate);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.DownloadAsync());
            Assert.AreEqual(1, fixture.Requests.Count);
        }
    }

    [TestMethod]
    public async Task StoreInstallationNeverContactsGitHubAndPortableDoesNotSilentlyInstall()
    {
        await using (var store = new Fixture(new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Store)))
        {
            await store.Updater.CheckAsync();
            Assert.AreEqual(UpdateState.StoreManaged, store.Updater.Snapshot.State);
            Assert.AreEqual(0, store.Requests.Count);
        }
        await using var portable = new Fixture(new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Portable));
        await portable.Updater.CheckAsync();
        Assert.AreEqual(UpdateState.Available, portable.Updater.Snapshot.State);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => portable.Updater.DownloadAsync());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => portable.Updater.InstallAsync());
        Assert.AreEqual(0, portable.Installer.Calls);
    }

    [TestMethod]
    public async Task ManualChecksRespectCooldownAndUsePersistedEtagFor304()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.CheckAsync();
        Assert.AreEqual(1, fixture.Requests.Count, "Repeated manual clicks must not burst requests.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Responder = (request, _) =>
        {
            Assert.AreEqual("\"release-1\"", request.Headers.IfNoneMatch.Single().ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        };
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
        Assert.AreEqual("1.1.0", fixture.Updater.Snapshot.Candidate!.Version);
        Assert.AreEqual(2, fixture.Requests.Count);
    }

    [TestMethod]
    public async Task ServerRetryAfterAndRateLimitResetPersistAcrossRestartAndManualClicks()
    {
        await using var fixture = new Fixture();
        fixture.Responder = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", fixture.Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        };
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        Assert.IsTrue(fixture.Updater.Snapshot.NextCheckUtc >= fixture.Clock.GetUtcNow().AddHours(1));
        await fixture.RestartAsync(); fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(1, fixture.Requests.Count);
        Assert.AreEqual(UpdateState.Deferred, fixture.Updater.Snapshot.State);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Responder = null; await fixture.Updater.CheckAsync();
        Assert.AreEqual(2, fixture.Requests.Count);
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task FailedChecksBackOffExponentiallyAndNeverRetryInsideOneCall()
    {
        await using var fixture = new Fixture();
        fixture.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(1, fixture.Requests.Count);
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Updater.Snapshot.NextCheckUtc);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6)); await fixture.Updater.CheckAsync();
        Assert.AreEqual(2, fixture.Requests.Count);
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddMinutes(10), fixture.Updater.Snapshot.NextCheckUtc);
    }

    [TestMethod]
    public async Task AutomaticChecksHonorConfiguredIntervalAndManualChecksCanRunAfterCooldown()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.SavePreferencesAsync(new(CheckIntervalHours: 4));
        await fixture.Updater.CheckAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(3)); await fixture.Updater.CheckAsync(manual: false);
        Assert.AreEqual(1, fixture.Requests.Count);
        await fixture.Updater.CheckAsync(); Assert.AreEqual(2, fixture.Requests.Count);
        Assert.IsTrue(fixture.Updater.Snapshot.NextCheckUtc >= fixture.Clock.GetUtcNow().AddHours(4));
        Assert.IsTrue(fixture.Updater.Snapshot.NextCheckUtc <= fixture.Clock.GetUtcNow().AddHours(4).AddMinutes(5));
    }

    [TestMethod]
    public async Task NewerCandidateRemovesOldReadyPackageBeforeIssuingNextDownload()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var previous = fixture.Updater.Snapshot.ReadyPackagePath!;
        Assert.IsTrue(File.Exists(previous));
        fixture.Clock.Advance(TimeSpan.FromMinutes(2)); fixture.Manifest = Manifest("1.2.0");
        await fixture.Updater.CheckAsync();
        Assert.IsTrue(File.Exists(previous), "Checking alone must not erase a verified package.");
        Assert.IsNull(fixture.Updater.Snapshot.ReadyPackagePath, "An older ready package must not remain installable once a newer candidate is selected.");
        fixture.Responder = (request, _) =>
        {
            Assert.IsFalse(File.Exists(previous), "Remove the previous download before requesting the new installer.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        };
        await fixture.Updater.DownloadAsync();
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        Assert.IsTrue(File.Exists(fixture.Updater.Snapshot.ReadyPackagePath));
        Assert.AreEqual(1, Directory.GetFiles(fixture.Cache, "pending-*").Length);
    }

    [TestMethod]
    public async Task DeleteDownloadsRemovesAllCurrentPreviousAndPartialVariantsWithoutDeletingPreferencesOrFeed()
    {
        await using var fixture = new Fixture();
        var preferences = new UpdatePreferences(false, true, false, 12);
        await fixture.Updater.SavePreferencesAsync(preferences);
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var current = fixture.Updater.Snapshot.ReadyPackagePath!;
        foreach (var name in new[]
        {
            "pending-CloudBay-0.8.0-win-x64-release-setup.exe",
            "pending-CloudBay-0.9.0-win-x64-debug-setup.msi",
            "partial-CloudBay-1.1.0-win-x64-debug-setup.exe",
            "PARTIAL-CLOUDBAY-0.9.0-win-x64-release-portable.zip"
        }) await File.WriteAllBytesAsync(Path.Combine(fixture.Cache, name), Payload);
        var unrelated = Path.Combine(fixture.Cache, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        var nested = Path.Combine(fixture.Cache, "personal"); Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "pending-CloudBay-user-file.exe"), Payload);
        var preferencesBefore = await File.ReadAllBytesAsync(Path.Combine(fixture.Cache, "preferences.json"));

        await fixture.Updater.DeleteDownloadsAsync();

        Assert.IsFalse(File.Exists(current));
        Assert.IsFalse(Directory.GetFiles(fixture.Cache).Any(path => Path.GetFileName(path).StartsWith("pending-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("partial-", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(preferencesBefore, await File.ReadAllBytesAsync(Path.Combine(fixture.Cache, "preferences.json")));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(unrelated));
        Assert.IsTrue(File.Exists(Path.Combine(nested, "pending-CloudBay-user-file.exe")));
        Assert.AreEqual(preferences, fixture.Updater.Preferences);
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
        Assert.AreEqual("1.1.0", fixture.Updater.Snapshot.Candidate!.Version);
        Assert.IsNull(fixture.Updater.Snapshot.ReadyPackagePath);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.InstallAsync());
        await fixture.RestartAsync();
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
        Assert.AreEqual(preferences, fixture.Updater.Preferences);
        Assert.AreEqual("1.1.0", fixture.Updater.Snapshot.Candidate!.Version);
    }

    [TestMethod]
    public async Task DeletedUpdateStaysSuppressedForAutomaticDownloadAcrossRestartUntilManualDownload()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.SavePreferencesAsync(new(false, true));
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        await fixture.Updater.DeleteDownloadsAsync(); await fixture.RestartAsync();
        fixture.Updater.Start();
        await WaitUntilAsync(() => fixture.Clock.WaitingTimers == 1);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => fixture.Clock.WaitingTimers == 1);
        Assert.AreEqual(2, fixture.Requests.Count, "Deleting a download must not immediately trigger the same automatic download.");
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);

        await fixture.Updater.DownloadAsync();
        Assert.AreEqual(3, fixture.Requests.Count);
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        Assert.IsTrue(File.Exists(fixture.Updater.Snapshot.ReadyPackagePath));
        await fixture.RestartAsync();
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task NewReleaseCanAutomaticallyDownloadAfterThePreviousReleaseWasDeleted()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.SavePreferencesAsync(new(false, true));
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        await fixture.Updater.DeleteDownloadsAsync(); await fixture.RestartAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2)); fixture.Manifest = Manifest("1.2.0");
        await fixture.Updater.CheckAsync();
        fixture.Updater.Start();
        await WaitUntilAsync(() => fixture.Updater.Snapshot.State == UpdateState.Ready);
        Assert.AreEqual("1.2.0", fixture.Updater.Snapshot.Candidate!.Version);
        Assert.AreEqual(4, fixture.Requests.Count);
        Assert.AreEqual(1, Directory.GetFiles(fixture.Cache, "pending-*").Length);
    }

    [TestMethod]
    public async Task DeleteDownloadsRejectsAnInstallerHandoffAndRetainsTheVerifiedPackage()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var path = fixture.Updater.Snapshot.ReadyPackagePath!;
        await fixture.Updater.InstallAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.DeleteDownloadsAsync());
        Assert.AreEqual(UpdateState.Installing, fixture.Updater.Snapshot.State);
        Assert.AreEqual(path, fixture.Updater.Snapshot.ReadyPackagePath);
        CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task DeleteDownloadsRejectsAnActiveWorkerLockWithoutClearingReadyState()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var path = fixture.Updater.Snapshot.ReadyPackagePath!;
        using (new FileStream(Path.Combine(fixture.Cache, "update-install.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.DeleteDownloadsAsync());
            Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
            Assert.AreEqual(path, fixture.Updater.Snapshot.ReadyPackagePath);
            Assert.IsTrue(File.Exists(path));
        }
        await fixture.Updater.DeleteDownloadsAsync();
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task InitializationRefusesToPruneDownloadsWhileAnUpdateWorkerOwnsTheCache()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var path = fixture.Updater.Snapshot.ReadyPackagePath!;
        await fixture.Updater.DisposeAsync();
        var orphan = Path.Combine(fixture.Cache, "partial-CloudBay-0.9.0-win-x64-debug-setup.msi");
        await File.WriteAllBytesAsync(orphan, Payload);
        using (new FileStream(Path.Combine(fixture.Cache, "update-install.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            fixture.CreateUpdater();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.InitializeAsync());
            Assert.IsTrue(File.Exists(path)); Assert.IsTrue(File.Exists(orphan));
        }
        await fixture.Updater.InitializeAsync();
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        Assert.IsTrue(File.Exists(path)); Assert.IsFalse(File.Exists(orphan));
    }

    [TestMethod]
    public async Task DeleteDownloadsHandlesAStaleWorkerLockAndAnEmptyCacheIdempotently()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Cache, "update-install.lock"), "stale");
        await fixture.Updater.DeleteDownloadsAsync(); await fixture.Updater.DeleteDownloadsAsync();
        Assert.AreEqual(UpdateState.Idle, fixture.Updater.Snapshot.State);
        Assert.IsNull(fixture.Updater.Snapshot.ReadyPackagePath);
        Assert.AreEqual(0, fixture.Requests.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Cache, "preferences.json")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Cache, "state.json")));
    }

    [TestMethod]
    public async Task DeleteDownloadsRefusesLinkedOrphansWithoutTouchingTheirTargets()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var target = Path.Combine(fixture.Cache, "important.txt"); await File.WriteAllTextAsync(target, "keep");
        var link = Path.Combine(fixture.Cache, "partial-CloudBay-0.9.0-win-x64-release-setup.exe");
        File.CreateSymbolicLink(link, target);
        try
        {
            await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Updater.DeleteDownloadsAsync());
            Assert.AreEqual("keep", await File.ReadAllTextAsync(target));
            Assert.IsNull(fixture.Updater.Snapshot.ReadyPackagePath);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.InstallAsync());
        }
        finally { File.Delete(link); }
        await fixture.Updater.DeleteDownloadsAsync();
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task InvalidNewManifestRetainsPreviouslyVerifiedPendingInstaller()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var previous = fixture.Updater.Snapshot.ReadyPackagePath!;
        fixture.Clock.Advance(TimeSpan.FromMinutes(2)); fixture.Manifest = Manifest("1.2.0") with { Repository = "attacker/CloudBay" };
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        Assert.IsTrue(File.Exists(previous)); Assert.AreEqual(previous, fixture.Updater.Snapshot.ReadyPackagePath);
        Assert.AreEqual("1.1.0", fixture.Updater.Snapshot.Candidate!.Version);
    }

    [TestMethod]
    public async Task IntegrityFailureAndTruncatedBodyNeverCreateReadyPackage()
    {
        foreach (var kind in new[] { "sha", "size", "truncated" })
        {
            await using var fixture = new Fixture();
            await fixture.Updater.CheckAsync();
            fixture.Responder = (_, _) =>
            {
                var bytes = kind == "sha" ? Enumerable.Repeat((byte)1, Payload.Length).ToArray() : Payload[..^1];
                var content = new ByteArrayContent(bytes);
                if (kind == "truncated") content.Headers.ContentLength = Payload.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            };
            await fixture.Updater.DownloadAsync();
            Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State, kind);
            Assert.IsNull(fixture.Updater.Snapshot.ReadyPackagePath);
            Assert.AreEqual(0, Directory.GetFiles(fixture.Cache, "pending-*").Length);
            Assert.AreEqual(0, Directory.GetFiles(fixture.Cache, "partial-*").Length);
            Assert.AreEqual(0, fixture.Installer.Calls);
        }
    }

    [TestMethod]
    public async Task CancellationDeletesPartialPackageAndRecoveryCannotInstallIt()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new CancelOnReadStream(Payload, cancellation)) });
        try { await fixture.Updater.DownloadAsync(cancellation.Token); Assert.Fail("An interrupted download must report cancellation."); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, Directory.GetFiles(fixture.Cache, "partial-*").Length);
        await fixture.RestartAsync();
        Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Updater.InstallAsync());
    }

    [TestMethod]
    public async Task CachedInstallerIsRehashedAtRecoveryAndAgainImmediatelyBeforeInstall()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        await fixture.RestartAsync(); Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        var path = fixture.Updater.Snapshot.ReadyPackagePath!;
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)4, Payload.Length).ToArray());
        await fixture.Updater.InstallAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        Assert.AreEqual(0, fixture.Installer.Calls); Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task SuccessfulInstallPreservesImmutableIdentityAndOnlyPassesVerifiedCandidate()
    {
        await using var fixture = new Fixture(new("1.0.0", UpdateBuildFlavor.Debug, UpdateInstallerKind.Msi));
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync(); await fixture.Updater.InstallAsync();
        Assert.AreEqual(1, fixture.Installer.Calls);
        Assert.AreEqual(fixture.Updater.Identity, fixture.Installer.Package!.Identity);
        Assert.AreEqual("1.1.0", fixture.Installer.Package.Candidate.Version);
        Assert.IsTrue(fixture.Installer.Package.Path.StartsWith(fixture.Cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(fixture.Installer.Package.Path));
    }

    [TestMethod]
    public async Task ConcurrentDownloadRequestsSerializeAndReuseOneVerifiedInstaller()
    {
        await using var fixture = new Fixture(); await fixture.Updater.CheckAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Updater.DownloadAsync()));
        Assert.AreEqual(2, fixture.Requests.Count, "One feed request and one installer request are sufficient.");
        Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task CrossProcessCacheOwnershipPreventsTwoWriters()
    {
        await using var fixture = new Fixture(); await fixture.Updater.InitializeAsync();
        await using var other = new UpdateCoordinator(fixture.Updater.Identity, fixture.Cache);
        await Assert.ThrowsExceptionAsync<IOException>(() => other.InitializeAsync());
        await fixture.Updater.CheckAsync(); Assert.AreEqual(UpdateState.Available, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task CorruptStateAndChangedInstalledIdentityCannotExecuteCachedInstaller()
    {
        await using var fixture = new Fixture(); await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        await fixture.Updater.DisposeAsync(); await File.WriteAllTextAsync(Path.Combine(fixture.Cache, "state.json"), "{broken");
        fixture.CreateUpdater(); await fixture.Updater.InitializeAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        Assert.AreEqual(0, Directory.GetFiles(fixture.Cache, "pending-*").Length);
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        await fixture.Updater.DisposeAsync(); fixture.Identity = new("1.1.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe);
        fixture.CreateUpdater(); await fixture.Updater.InitializeAsync();
        Assert.IsNull(fixture.Updater.Snapshot.Candidate);
        Assert.AreEqual(0, Directory.GetFiles(fixture.Cache, "pending-*").Length);
    }

    [TestMethod]
    public async Task PreferencesPersistSeparatelyAndAutoInstallImpliesDownload()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.SavePreferencesAsync(new(false, false, true, 0));
        await fixture.RestartAsync();
        Assert.IsFalse(fixture.Updater.Preferences.AutomaticChecks);
        Assert.IsTrue(fixture.Updater.Preferences.AutomaticallyDownload);
        Assert.IsTrue(fixture.Updater.Preferences.AutomaticallyInstall);
        Assert.AreEqual(1, fixture.Updater.Preferences.CheckIntervalHours);
        Assert.IsFalse(Directory.GetFiles(fixture.Cache).Any(path => Path.GetFileName(path) == "settings.json"));
    }

    [TestMethod]
    public async Task AutomaticDownloadAndInstallAreOptInAndWorkWithOneBackgroundScheduler()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.SavePreferencesAsync(new(AutomaticallyInstall: true));
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Installer.Installed = () => installed.TrySetResult();
        fixture.Updater.Start(); fixture.Updater.Start();
        await installed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, fixture.Requests.Count);
        Assert.AreEqual(1, fixture.Installer.Calls);
        Assert.AreEqual(UpdateState.Installing, fixture.Updater.Snapshot.State);
    }

    [DataTestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task DisablingAutomaticUpdatesWinsBeforeAnAlreadyQueuedActionStarts(bool install)
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync();
        if (install) await fixture.Updater.DownloadAsync();
        await fixture.Updater.SavePreferencesAsync(new(false, true, install));
        var requestsBeforeQueuedAction = fixture.Requests.Count;
        var queued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A scheduler can decide to act immediately before the user's save enters the
        // operation gate. Its already-decided action must honor that later save. The
        // notification runs before SavePreferences releases its gate, so this queues
        // the stale action deterministically instead of relying on thread timing.
        fixture.Updater.Changed += _ =>
        {
            if (fixture.Updater.Preferences.AutomaticallyDownload || fixture.Updater.Preferences.AutomaticallyInstall || queued.Task.IsCompleted) return;
            var action = (Task)typeof(UpdateCoordinator).GetMethod(install ? "InstallCoreAsync" : "DownloadCoreAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(fixture.Updater, [false, CancellationToken.None, null])!;
            if (action.IsCompleted) queued.TrySetException(new InvalidOperationException("The automatic action did not wait for the preference operation gate."));
            else queued.TrySetResult(action);
        };
        var disable = fixture.Updater.SavePreferencesAsync(new(false, false, false));
        var automatic = await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(disable, automatic).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(fixture.Updater.Preferences.AutomaticallyDownload);
        Assert.IsFalse(fixture.Updater.Preferences.AutomaticallyInstall);
        Assert.AreEqual(requestsBeforeQueuedAction, fixture.Requests.Count,
            "A queued automatic operation must honor the preference that wins the operation gate.");
        Assert.AreEqual(0, fixture.Installer.Calls);
        Assert.AreEqual(install ? UpdateState.Ready : UpdateState.Available, fixture.Updater.Snapshot.State);
        if (install)
        {
            await fixture.Updater.InstallAsync(); Assert.AreEqual(1, fixture.Installer.Calls);
        }
        else
        {
            await fixture.Updater.DownloadAsync(); Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        }
    }

    [TestMethod]
    public async Task FreshInstallationWithoutPublishedReleaseIsHandledWithoutRetryBurst()
    {
        await using var fixture = new Fixture();
        fixture.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        await fixture.Updater.CheckAsync(); Assert.AreEqual(UpdateState.UpToDate, fixture.Updater.Snapshot.State);
        StringAssert.Contains(fixture.Updater.Snapshot.Message, "No update");
        await fixture.Updater.CheckAsync(); Assert.AreEqual(1, fixture.Requests.Count);
    }

    [TestMethod]
    public async Task UntrustedRedirectIsRejectedBeforeSendingAnyForeignRequest()
    {
        foreach (var redirect in new[] { "http://github.com/TahsinFaiyaz30/CloudBay/releases/download/v1.1.0/updates-v1.json",
                     "https://example.com/payload", "https://github.com/attacker/repo/releases/download/v1.1.0/updates-v1.json",
                     "https://github.com:444/TahsinFaiyaz30/CloudBay/releases/download/v1.1.0/updates-v1.json" })
        {
            await using var fixture = new Fixture();
            fixture.Responder = (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(redirect); return Task.FromResult(response);
            };
            await fixture.Updater.CheckAsync();
            Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
            Assert.AreEqual(1, fixture.Requests.Count, redirect);
        }
    }

    [TestMethod]
    public async Task TrustedGitHubReleaseCdnRedirectRetainsHashVerification()
    {
        await using var fixture = new Fixture(); await fixture.Updater.CheckAsync();
        fixture.Responder = (request, _) =>
        {
            if (request.RequestUri!.Host == "github.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset-2e65be/id?token=fixture");
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        };
        await fixture.Updater.DownloadAsync(); Assert.AreEqual(UpdateState.Ready, fixture.Updater.Snapshot.State);
        Assert.AreEqual(3, fixture.Requests.Count);
    }

    [TestMethod]
    public async Task OlderFeedCannotReplaceANewerCandidateEvenWhenStillAboveInstalledVersion()
    {
        await using var fixture = new Fixture(); fixture.Manifest = Manifest("1.2.0");
        await fixture.Updater.CheckAsync(); fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Manifest = Manifest("1.1.0"); await fixture.Updater.CheckAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        Assert.AreEqual("1.2.0", fixture.Updater.Snapshot.Candidate!.Version);
    }

    [TestMethod]
    public async Task ManifestJsonRejectsIntegerAndNumericStringPackageVariants()
    {
        foreach (var field in new[] { "buildFlavor", "installerKind" })
            foreach (var value in new object[] { 0, "0", "1" })
            {
                await using var fixture = new Fixture();
                var json = JsonNode.Parse(JsonSerializer.Serialize(fixture.Manifest, UpdateManifestRules.JsonOptions))!;
                json["assets"]![0]![field] = JsonSerializer.SerializeToNode(value);
                fixture.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") });
                await fixture.Updater.CheckAsync();
                Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
                Assert.IsNull(fixture.Updater.Snapshot.Candidate);
                Assert.AreEqual(1, fixture.Requests.Count);
            }
    }

    [TestMethod]
    public void ReleaseManifestRejectsWrongRepositoryDuplicateVariantsAndUnsafeInstallerNames()
    {
        var identity = new InstalledUpdateIdentity("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe);
        var valid = Manifest(); var asset = valid.Assets[0];
        foreach (var invalid in new[]
        {
            valid with { Repository = "other/repo" }, valid with { Tag = "v9.0.0" }, valid with { SchemaVersion = 2 },
            valid with { Assets = [asset, asset] }, valid with { Assets = [asset with { FileName = "../CloudBay-malware.exe" }] },
            valid with { Assets = [asset with { FileName = "CloudBay-..-setup.exe" }] },
            valid with { Assets = [asset with { FileName = "CloudBay-setup.msi" }] },
            valid with { Assets = [asset with { Size = 1_073_741_825 }] },
            valid with { Assets = [asset with { Sha256 = "bad" }] }, valid with { Version = "1.2.3-beta" }
        }) Assert.ThrowsException<InvalidDataException>(() => UpdateManifestRules.Select(invalid, identity));
    }

    [TestMethod]
    public async Task ExistingNonOwnedDirectoryIsNeverAcceptedAsACleanupTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBayUpdaterTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var unrelated = Path.Combine(root, "important.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        try
        {
            Assert.ThrowsException<IOException>(() => new UpdateCoordinator(new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe), root));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(unrelated));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LinkedCacheDirectoryIsRejectedWithoutTouchingItsTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBayUpdaterTests-" + Guid.NewGuid().ToString("N"));
        var target = root + "-target"; Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "important.txt"), "keep");
        try
        {
            Directory.CreateSymbolicLink(root, target);
            Assert.ThrowsException<IOException>(() => new UpdateCoordinator(new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe), root));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(target, "important.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root); Directory.Delete(target, true); }
    }

    [TestMethod]
    public void ManifestInstallerNameMustMatchVersionFlavorArchitectureAndKind()
    {
        var identity = new InstalledUpdateIdentity("1.0.0", UpdateBuildFlavor.Debug, UpdateInstallerKind.Msi);
        var valid = Manifest();
        var selected = valid.Assets.Single(asset => asset.BuildFlavor == UpdateBuildFlavor.Debug && asset.InstallerKind == UpdateInstallerKind.Msi);
        foreach (var name in new[]
        {
            "CloudBay-1.1.0-win-x64-release-setup.msi", "CloudBay-1.1.0-win-arm64-debug-setup.msi",
            "CloudBay-9.0.0-win-x64-debug-setup.msi", "CloudBay-1.1.0-win-x64-debug-setup.exe"
        }) Assert.ThrowsException<InvalidDataException>(() => UpdateManifestRules.Select(valid with { Assets = [selected with { FileName = name }] }, identity));
    }

    [TestMethod]
    public async Task PublishedVersionCannotSilentlyReplacePreviouslyVerifiedInstaller()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        var path = fixture.Updater.Snapshot.ReadyPackagePath!;
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Manifest = fixture.Manifest with { Assets = fixture.Manifest.Assets.Select(asset => asset with { Sha256 = new string('a', 64) }).ToArray() };
        await fixture.Updater.CheckAsync();
        Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
        Assert.AreEqual(path, fixture.Updater.Snapshot.ReadyPackagePath);
        await fixture.Updater.InstallAsync();
        Assert.AreEqual(1, fixture.Installer.Calls);
        CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(fixture.Installer.Package!.Path));
    }

    [TestMethod]
    public async Task RepeatedChecksDownloadsAndInstallClicksCannotLaunchDuplicateInstallers()
    {
        await using var fixture = new Fixture();
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Updater.InstallAsync()));
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.Updater.CheckAsync(); await fixture.Updater.DownloadAsync();
        Assert.AreEqual(1, fixture.Installer.Calls);
        Assert.AreEqual(2, fixture.Requests.Count);
        Assert.AreEqual(UpdateState.Installing, fixture.Updater.Snapshot.State);
    }

    [TestMethod]
    public async Task DisposingCancelsAnActiveManualDownloadWithoutWaitingForTheNetwork()
    {
        await using var fixture = new Fixture(); await fixture.Updater.CheckAsync();
        var input = new BlockedReadStream();
        fixture.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) });
        var download = fixture.Updater.DownloadAsync();
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Updater.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        try { await download; Assert.Fail("Shutdown must cancel the active download."); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, Directory.GetFiles(fixture.Cache, "partial-*").Length);
    }

    [TestMethod]
    public async Task StalledHeadersAndBodyTimeOutAndPersistAQuietRetry()
    {
        foreach (var body in new[] { false, true })
        {
            await using var fixture = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var input = new BlockedReadStream();
            fixture.Responder = async (_, token) =>
            {
                if (body) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
                entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("A stalled request cannot complete.");
            };
            var check = fixture.Updater.CheckAsync();
            await (body ? input.Entered.Task : entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            await check.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(UpdateState.Error, fixture.Updater.Snapshot.State);
            StringAssert.Contains(fixture.Updater.Snapshot.Message, body ? "stopped responding" : "respond in time");
            await fixture.RestartAsync(); await fixture.Updater.CheckAsync();
            Assert.AreEqual(1, fixture.Requests.Count);
        }
    }

    [TestMethod]
    public async Task DanglingOwnerAndMetadataLinksAreRejectedBeforeWritingTheirTargets()
    {
        foreach (var name in new[] { ".cloudbay-update-cache-v1", "state.json" })
        {
            var cache = Path.Combine(Path.GetTempPath(), "CloudBayUpdaterTests-" + Guid.NewGuid().ToString("N"));
            var missingTarget = cache + "-never-created";
            Directory.CreateDirectory(cache);
            try
            {
                if (name == "state.json") await File.WriteAllTextAsync(Path.Combine(cache, ".cloudbay-update-cache-v1"), "CloudBay update cache v1\n");
                var link = Path.Combine(cache, name); File.CreateSymbolicLink(link, missingTarget);
                if (name == "state.json")
                {
                    await using var updater = new UpdateCoordinator(new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe), cache);
                    await Assert.ThrowsExceptionAsync<IOException>(() => updater.InitializeAsync());
                }
                else Assert.ThrowsException<IOException>(() => new UpdateCoordinator(new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe), cache));
                Assert.IsFalse(File.Exists(missingTarget));
                File.Delete(link);
            }
            finally { Directory.Delete(cache, true); }
        }
    }

    [DataTestMethod]
    [DataRow("1.0.0", true)] [DataRow("0.9.42", true)] [DataRow("255.255.65535", true)]
    [DataRow("01.0.0", false)] [DataRow("1.0", false)] [DataRow("1.0.0.0", false)]
    [DataRow("1.0.0-preview", false)] [DataRow("256.0.0", false)] [DataRow("1.256.0", false)]
    [DataRow("65536.0.0", false)] [DataRow("v1.0.0", false)] [DataRow("1.0.65536", false)]
    public void StableVersionParserRejectsAmbiguousOrOutOfRangeVersions(string version, bool expected) =>
        Assert.AreEqual(expected, UpdateVersion.TryParse(version, out _));

    private static UpdateManifest Manifest(string version = "1.1.0")
    {
        var assets = new List<UpdateAsset>();
        foreach (var flavor in new[] { UpdateBuildFlavor.Release, UpdateBuildFlavor.Debug })
            foreach (var kind in new[] { UpdateInstallerKind.Exe, UpdateInstallerKind.Msi, UpdateInstallerKind.Portable })
            {
                var suffix = kind == UpdateInstallerKind.Portable ? "portable.zip" : kind == UpdateInstallerKind.Exe ? "setup.exe" : "setup.msi";
                assets.Add(new(flavor, kind, "x64", $"CloudBay-{version}-win-x64-{flavor.ToString().ToLowerInvariant()}-{suffix}",
                    Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant()));
            }
        return new(1, UpdateManifestRules.Repository, version, "v" + version, assets);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public InstalledUpdateIdentity Identity;
        public readonly string Cache = Path.Combine(Path.GetTempPath(), "CloudBayUpdaterTests-" + Guid.NewGuid().ToString("N"));
        public readonly MutableClock Clock = new();
        public readonly FakeInstaller Installer = new();
        public readonly List<string> Requests = [];
        public UpdateManifest Manifest = UpdateCoordinatorTests.Manifest();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder;
        public UpdateCoordinator Updater = null!;
        public Fixture(InstalledUpdateIdentity? identity = null)
        { Identity = identity ?? new("1.0.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe); CreateUpdater(); }
        public void CreateUpdater() => Updater = new(Identity, Cache, Installer, new FakeHandler(async (request, token) =>
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (Responder is not null) return await Responder(request, token);
            var content = request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToUtf8Bytes(Manifest, UpdateManifestRules.JsonOptions) : Payload;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            response.Headers.ETag = new EntityTagHeaderValue("\"release-1\""); return response;
        }), Clock);
        public async Task RestartAsync() { await Updater.DisposeAsync(); CreateUpdater(); await Updater.InitializeAsync(); }
        public async ValueTask DisposeAsync()
        {
            await Updater.DisposeAsync();
            var absolute = Path.GetFullPath(Cache);
            if (!absolute.StartsWith(Path.Combine(Path.GetTempPath(), "CloudBayUpdaterTests-"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid test cleanup path.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class MutableClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        public int WaitingTimers { get { lock (_gate) return _timers.Count(timer => timer.Due is not null); } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan interval)
        {
            ManualTimer[] due;
            lock (_gate) { _now += interval; due = _timers.Where(timer => timer.Due <= _now).ToArray(); }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(MutableClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due;
            private TimeSpan _period; private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    _period = period; return true;
                }
            }
            public void Fire()
            {
                lock (owner._gate)
                {
                    if (_disposed || Due is null || Due > owner._now) return;
                    Due = _period > TimeSpan.Zero ? owner._now + _period : null;
                }
                callback(state);
            }
            public void Dispose() { lock (owner._gate) { _disposed = true; Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class FakeInstaller : IUpdateInstaller
    {
        public int Calls; public VerifiedUpdatePackage? Package; public Action? Installed;
        public Task InstallAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
        { Calls++; Package = package; Installed?.Invoke(); return Task.CompletedTask; }
    }
    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var count = Read(buffer.Span[..Math.Min(buffer.Length, 5)]); cancellation.Cancel(); return ValueTask.FromResult(count); }
    }
    private sealed class BlockedReadStream : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
}
