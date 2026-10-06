using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class TransferJobEngineTests
{
    [TestMethod]
    public async Task SelectedRenameReceiptSurvivesPauseAndRestartWithoutUploadingAgain()
    {
        await using var fixture = new Fixture(1, conflicts: TransferConflictPolicy.Rename);
        fixture.Source.OnePage = true;
        fixture.Destination.ReturnRenamedReceipt = true;
        fixture.Destination.HoldVerification = true;
        var work = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.VerificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.PauseAsync(fixture.Plan.Id); await work;
        await fixture.RestartAsync();
        fixture.Destination.HoldVerification = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
        Assert.AreEqual(1, fixture.Source.Pages.Count);
    }

    [TestMethod]
    public async Task RenamePolicyRejectsAnUnrelatedReceiptBeforeVerificationOrMove()
    {
        await using var fixture = new Fixture(1, TransferOperation.Move, TransferConflictPolicy.Rename);
        fixture.Source.OnePage = true;
        fixture.Destination.UnexpectedReceiptPath = "unrelated.txt";
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Attention, fixture.Engine.Snapshots().Single().State);
        Assert.IsFalse(fixture.Destination.VerificationEntered.Task.IsCompleted);
        Assert.AreEqual(0, fixture.Source.Deleted.Count);
    }

    [TestMethod]
    public async Task DiscoveryAndVerifiedCountersResumeWithoutRecountingOrReplayingCompletedFiles()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.HoldSecondPage = true;
        var run = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Source.SecondPageEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Destination.FirstVerified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.PauseAsync(fixture.Plan.Id);
        await run;
        Assert.AreEqual(1L, fixture.Engine.Snapshots().Single().CompletedFiles);
        Assert.AreEqual(6L, fixture.Engine.Snapshots().Single().TransferredBytes);
        fixture.Source.HoldSecondPage = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = fixture.Engine.Snapshots().Single();
        Assert.AreEqual(TransferJobState.Completed, snapshot.State, snapshot.Error);
        Assert.AreEqual(2L, snapshot.FileCount);
        Assert.AreEqual(2L, snapshot.CompletedFiles);
        Assert.AreEqual(12L, snapshot.TransferredBytes);
        Assert.AreEqual(0L, snapshot.RemainingBytes);
        Assert.AreEqual(1, fixture.Source.Pages.Count(cursor => cursor is null), "Committed discovery pages must not be rescanned.");
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
        Assert.AreEqual(1, fixture.Destination.Uploads["second"]);
    }

    [TestMethod]
    public async Task DurableAcknowledgedFileResumesAtItsSavedRangeAndRestoresProgressAfterRestart()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.OnePage = true;
        fixture.Destination.HoldAfterCheckpoint = true;
        var work = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.CheckpointSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.PauseAsync(fixture.Plan.Id); await work;
        Assert.AreEqual(3L, fixture.Engine.Snapshots().Single().TransferredBytes);
        await fixture.RestartAsync();
        Assert.AreEqual(3L, fixture.Engine.Snapshots().Single().TransferredBytes);
        fixture.Destination.HoldAfterCheckpoint = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        CollectionAssert.AreEqual(new long[] { 0, 3 }, fixture.Destination.StartOffsets.ToArray());
        Assert.AreEqual(1, fixture.Source.Pages.Count);
    }

    [TestMethod]
    public async Task SavedUploadReceiptsSurvivePauseAndRestartWithoutDuplicateUpload()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.OnePage = true;
        fixture.Destination.HoldVerification = true;
        var work = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.VerificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.PauseAsync(fixture.Plan.Id); await work;
        Assert.AreEqual(6L, fixture.Engine.Snapshots().Single().TransferredBytes);
        Assert.AreEqual(0L, fixture.Engine.Snapshots().Single().RemainingBytes, "Acknowledged payload is no longer remaining while verification is pending.");
        Assert.AreEqual(0L, fixture.Engine.Snapshots().Single().CompletedFiles);
        await fixture.RestartAsync();
        fixture.Destination.HoldVerification = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
        Assert.AreEqual(1L, fixture.Engine.Snapshots().Single().CompletedFiles);
    }

    [TestMethod]
    public async Task MoveIntegrityFailureRetainsSourceAndRetryOnlyVerifiesExistingReceipt()
    {
        await using var fixture = new Fixture(1, TransferOperation.Move);
        fixture.Source.OnePage = true; fixture.Destination.BadIntegrity = true;
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Attention, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(0, fixture.Source.Deleted.Count);
        fixture.Destination.BadIntegrity = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(1, fixture.Source.Deleted.Count);
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
    }

    [TestMethod]
    public async Task ChangedSourceAfterAcknowledgmentNeverMovesOrReuploadsTheSource()
    {
        await using var fixture = new Fixture(1, TransferOperation.Move);
        fixture.Source.OnePage = true;
        fixture.Destination.OnUploaded = () => fixture.Source.Changed = true;
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Attention, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(0, fixture.Source.Deleted.Count);
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
        Assert.AreEqual(0, fixture.Source.Deleted.Count);
    }

    [TestMethod]
    public async Task LostMoveDeleteAcknowledgmentReconcilesExactMissingIdentity()
    {
        await using var fixture = new Fixture(1, TransferOperation.Move);
        fixture.Source.OnePage = true; fixture.Source.LoseDeleteAcknowledgment = true;
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Attention, fixture.Engine.Snapshots().Single().State);
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(1, fixture.Source.Deleted.Count);
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
    }

    [TestMethod]
    public async Task CancelRetainsDurablePlanAndCheckpointsAndRequiresExplicitResume()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.OnePage = true; fixture.Destination.HoldAfterCheckpoint = true;
        var work = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.CheckpointSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.CancelAsync(fixture.Plan.Id); await work;
        Assert.AreEqual(TransferJobState.Cancelled, fixture.Engine.Snapshots().Single().State);
        await fixture.RestartAsync();
        await fixture.Engine.RunAsync(fixture.Plan.Id);
        Assert.AreEqual(TransferJobState.Cancelled, fixture.Engine.Snapshots().Single().State);
        fixture.Destination.HoldAfterCheckpoint = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
    }

    [TestMethod]
    public async Task ExpiredDiscoveryPageReplayDoesNotRecountSavedIdentities()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.ReplayFirstOnSecondPage = true;
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(2L, fixture.Engine.Snapshots().Single().FileCount);
        Assert.AreEqual(12L, fixture.Engine.Snapshots().Single().TotalBytes);
        Assert.AreEqual(1, fixture.Destination.Uploads["first"]);
    }

    [TestMethod]
    public async Task InterruptedActiveJobsAreDistinguishedFromExplicitPauseOnStartup()
    {
        await using var fixture = new Fixture(1);
        await fixture.Engine.DisposeAsync();
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=false"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE transfer_jobs SET state=" + (int)TransferJobState.Running; command.ExecuteNonQuery();
        }
        using var journal = new TransferJobJournal(fixture.Database, new ProtectedCheckpoint());
        CollectionAssert.AreEqual(new[] { fixture.Plan.Id }, journal.RecoverableJobIds.ToArray());
        Assert.AreEqual(TransferJobState.Paused, journal.Snapshots().Single().State);
    }

    [TestMethod]
    public async Task GracefulShutdownPreservesAutomaticContinuationIntentWithoutChangingUserPause()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.OnePage = true; fixture.Destination.HoldAfterCheckpoint = true;
        _ = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.CheckpointSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.DisposeAsync();
        using var journal = new TransferJobJournal(fixture.Database,new ProtectedCheckpoint());
        CollectionAssert.AreEqual(new[]{fixture.Plan.Id},journal.RecoverableJobIds.ToArray());
        Assert.AreEqual(3L,journal.Snapshots().Single().TransferredBytes);
    }

    [TestMethod]
    public async Task MetadataJournalNeverContainsCloudPayloadOrPlaintextSessionSecrets()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.OnePage = true;
        fixture.Source.Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("UNIQUE-CLOUD-PAYLOAD-MUST-NOT-REACH-DISK-", 400)));
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        await fixture.Engine.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(fixture.DirectoryPath))
        {
            var contents = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.IsFalse(contents.Contains("UNIQUE-CLOUD-PAYLOAD-MUST-NOT-REACH-DISK-"));
            Assert.IsFalse(contents.Contains("signed-session-secret"));
        }
    }

    [TestMethod]
    public async Task LocalAdapterMovesOnlyVerifiedSourceAndPreservesConflictingDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudBayLocalAdapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sourcePath = Path.Combine(directory, "source"); var targetPath = Path.Combine(directory, "target");
            Directory.CreateDirectory(sourcePath); Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "file.txt"), "verified content");
            var source = new LocalTransferEndpoint(LocalTransferEndpoint.ForFolder(sourcePath));
            var destination = new LocalTransferEndpoint(LocalTransferEndpoint.ForFolder(targetPath));
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source.Location, destination.Location, TransferOperation.Move,
                TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            await using var engine = new TransferJobEngine(new(Path.Combine(directory,"jobs.sqlite"), new ProtectedCheckpoint()),
                location => location == source.Location ? source : destination, 2);
            await engine.CreateAsync(plan); await engine.RunAsync(plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(TransferJobState.Completed, engine.Snapshots().Single().State, engine.Snapshots().Single().Error);
            Assert.IsFalse(File.Exists(Path.Combine(sourcePath,"file.txt")));
            Assert.AreEqual("verified content", await File.ReadAllTextAsync(Path.Combine(targetPath,"file.txt")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task WorkerPreferenceCanIncreaseRunningJobWithoutLosingAcknowledgedCheckpoints()
    {
        await using var fixture = new Fixture(1);
        fixture.Destination.HoldAfterCheckpoint = true;
        var work = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.CheckpointSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, fixture.Destination.Uploads.Count);
        fixture.Engine.ConfigureWorkers(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Destination.Uploads.Count < 2) await Task.Delay(10, timeout.Token);
        await fixture.Engine.PauseAsync(fixture.Plan.Id); await work;
        Assert.AreEqual(6L, fixture.Engine.Snapshots().Single().TransferredBytes);
        fixture.Destination.HoldAfterCheckpoint = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(2, fixture.Destination.StartOffsets.Count(offset => offset == 3));
    }

    [TestMethod]
    public async Task SkippedFilesReduceRemainingBytesWithoutCountingPayloadAsTransferred()
    {
        await using var fixture = new Fixture(1);
        fixture.Source.OnePage = true; fixture.Destination.SkipUploads = true;
        await fixture.Engine.RunAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = fixture.Engine.Snapshots().Single();
        Assert.AreEqual(TransferJobState.Completed, snapshot.State);
        Assert.AreEqual(1L, snapshot.SkippedFiles);
        Assert.AreEqual(0L, snapshot.TransferredBytes);
        Assert.AreEqual(0L, snapshot.RemainingBytes);
        await fixture.RestartAsync();
        Assert.AreEqual(0L, fixture.Engine.Snapshots().Single().RemainingBytes);
    }

    [TestMethod]
    public void RenameConflictKeepsOriginalPathUntilTheAdapterFindsAConflict()
    {
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"),
            new("onedrive", "source", "drive", "folder", "", "Source"),
            new("b2", "target", "bucket", "", "target/", "Target"),
            TransferOperation.Copy, TransferConflictPolicy.Rename, [], DateTimeOffset.UtcNow);
        var folder = new TransferEntry("folder", "nested", "folder-version", 0, DateTimeOffset.UnixEpoch, IsFolder: true);
        Assert.AreEqual("nested", TransferJobEngine.Request(plan, folder).RelativePath);
        var file = folder with { Id = "file", RelativePath = "nested/file.txt", IsFolder = false, Size = 6 };
        var request = TransferJobEngine.Request(plan, file);
        Assert.AreEqual("nested/file.txt", request.RelativePath);
        Assert.AreEqual(TransferConflictPolicy.Rename, request.ConflictPolicy);
        Assert.AreEqual(request, TransferJobEngine.Request(plan, file));
    }

    [TestMethod]
    public void PlansRejectNestedWindowsFoldersAndAllowDistinctB2Prefixes()
    {
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), LocalTransferEndpoint.ForFolder(@"C:\Example\Source"),
            LocalTransferEndpoint.ForFolder(@"C:\Example\Source\Nested"), TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        Assert.ThrowsException<InvalidDataException>(() => TransferValidation.ValidatePlan(plan));
        TransferValidation.ValidatePlan(plan with
        {
            Source = new("b2", "account", "bucket", "", "source/", "Source"),
            Destination = new("b2", "account", "bucket", "", "destination/", "Destination")
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CloudBayTransferTests-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(DirectoryPath,"jobs.sqlite");
        public TestEndpoint Source { get; } = new(new("onedrive","source-account","drive","folder","","Source"));
        public TestEndpoint Destination { get; } = new(new("b2","target-account","bucket","","target/","Destination"));
        public TransferJobEngine Engine { get; private set; }
        public TransferJobPlan Plan { get; }
        private readonly int _workers;
        public Fixture(int workers, TransferOperation operation = TransferOperation.Copy, TransferConflictPolicy conflicts = TransferConflictPolicy.Fail)
        {
            _workers = workers; Directory.CreateDirectory(DirectoryPath);
            Engine = CreateEngine();
            Plan = new(Guid.NewGuid().ToString("N"), Source.Location, Destination.Location, operation, conflicts, [], DateTimeOffset.UtcNow);
            Engine.CreateAsync(Plan).GetAwaiter().GetResult();
        }
        private TransferJobEngine CreateEngine() => new(new(Database, new ProtectedCheckpoint()), location => location.Provider == "onedrive" ? Source : Destination, _workers);
        public async Task RestartAsync() { await Engine.DisposeAsync(); Engine = CreateEngine(); }
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath,true); }
    }

    private sealed class ProtectedCheckpoint : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => System.Security.Cryptography.ProtectedData.Protect(plaintext, "test-checkpoints"u8.ToArray(), DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => System.Security.Cryptography.ProtectedData.Unprotect(ciphertext, "test-checkpoints"u8.ToArray(), DataProtectionScope.CurrentUser);
    }

    private sealed class TestEndpoint(TransferLocation location) : ITransferEndpoint
    {
        public TransferLocation Location { get; } = location;
        public byte[] Payload = "abcdef"u8.ToArray();
        public bool OnePage, HoldSecondPage, HoldAfterCheckpoint, HoldVerification, BadIntegrity, Changed, LoseDeleteAcknowledgment, ReplayFirstOnSecondPage, SkipUploads;
        public bool ReturnRenamedReceipt;
        public string? UnexpectedReceiptPath;
        public List<string?> Pages { get; } = [];
        public ConcurrentDictionary<string,int> Uploads { get; } = new();
        public ConcurrentDictionary<string,bool> Deleted { get; } = new();
        public ConcurrentQueue<long> StartOffsets { get; } = new();
        public TaskCompletionSource SecondPageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstVerified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckpointSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource VerificationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnUploaded;
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null,CancellationToken cancellationToken=default) => Task.FromResult(new TransferFolderPage([],null));
        public async Task<TransferDiscoveryPage> DiscoverAsync(string? cursor=null,CancellationToken cancellationToken=default)
        {
            Pages.Add(cursor);
            if (cursor is not null)
            {
                SecondPageEntered.TrySetResult();
                if (HoldSecondPage) await Task.Delay(Timeout.Infinite,cancellationToken);
                return new(ReplayFirstOnSecondPage ? [Entry("first"), Entry("second")] : [Entry("second")],null);
            }
            return new([Entry("first")],OnePage ? null : "page-2");
        }
        private TransferEntry Entry(string id) => new(id,id+".txt","version-"+id,Payload.Length,DateTimeOffset.UnixEpoch,
            Convert.ToHexString(SHA1.HashData(Payload)).ToLowerInvariant());
        public ITransferSourceFile OpenSource(TransferEntry entry) => new TestSource(this,entry);
        public Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request,ITransferSourceFile source,TransferCheckpoint? checkpoint,CancellationToken cancellationToken=default) => Task.FromResult<TransferReceipt?>(null);
        public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request,ITransferSourceFile source,TransferCheckpoint? checkpoint,
            Func<TransferCheckpoint,CancellationToken,Task> saveCheckpoint,IProgress<TransferProgress>? progress=null,CancellationToken cancellationToken=default)
        {
            if (SkipUploads) throw new TransferSkippedException("Injected existing destination.");
            Uploads.AddOrUpdate(source.Entry.Id,1,(_,count)=>count+1);
            var offset=checkpoint?.AcknowledgedBytes??0; StartOffsets.Enqueue(offset);
            var half=source.Entry.Size/2;
            await using var stream=await source.OpenReadAsync(offset,source.Entry.Size-offset,cancellationToken);
            var bytes=new byte[(int)(source.Entry.Size-offset)]; await stream.ReadExactlyAsync(bytes,cancellationToken);
            if (offset==0)
            {
                await saveCheckpoint(new("b2","signed-session-secret",half),cancellationToken);
                progress?.Report(new(half,source.Entry.Size)); CheckpointSaved.TrySetResult();
                if (HoldAfterCheckpoint) await Task.Delay(Timeout.Infinite,cancellationToken);
            }
            progress?.Report(new(source.Entry.Size,source.Entry.Size)); OnUploaded?.Invoke();
            var target = UnexpectedReceiptPath ?? (ReturnRenamedReceipt
                ? CloudBay.Core.Sync.PathRules.ConflictFileName(request.RelativePath, " (CloudBay " + request.OperationId[..12] + ")") : request.RelativePath);
            return new("receipt-"+source.Entry.Id,target,"destination-version",source.Entry.Size,source.Entry.Sha1,request.OperationId);
        }
        public async Task VerifyAsync(TransferReceipt receipt,ITransferSourceFile source,CancellationToken cancellationToken=default)
        {
            VerificationEntered.TrySetResult(); if (HoldVerification) await Task.Delay(Timeout.Infinite,cancellationToken);
            if (BadIntegrity) throw new InvalidDataException("Injected destination checksum mismatch.");
            FirstVerified.TrySetResult();
        }
        public Task DeleteSourceAsync(TransferEntry entry,CancellationToken cancellationToken=default)
        {
            if (Changed) throw new TransferSourceChangedException("Injected source mutation.");
            Deleted[entry.Id]=true;
            if (LoseDeleteAcknowledgment) throw new IOException("Injected lost delete acknowledgment.");
            return Task.CompletedTask;
        }
        public Task<bool> IsSourceDeletedAsync(TransferEntry entry,CancellationToken cancellationToken=default)=>Task.FromResult(Deleted.ContainsKey(entry.Id));
        private sealed class TestSource(TestEndpoint owner,TransferEntry entry):ITransferSourceFile
        {
            public TransferEntry Entry{get;}=entry;
            public Task ValidateAsync(CancellationToken cancellationToken=default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(owner.Changed||owner.Deleted.ContainsKey(Entry.Id))throw new TransferSourceChangedException("Injected source mutation.");
                return Task.CompletedTask;
            }
            public async Task<Stream> OpenReadAsync(long offset,long length,CancellationToken cancellationToken=default)
            {await ValidateAsync(cancellationToken);return new MemoryStream(owner.Payload,(int)offset,(int)length,false);}
        }
    }
}
