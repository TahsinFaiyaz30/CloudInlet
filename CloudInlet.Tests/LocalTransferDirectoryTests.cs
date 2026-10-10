using System.Security.Cryptography;
using CloudInlet.Core;
using CloudInlet.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class LocalTransferDirectoryTests
{
    [TestMethod]
    public async Task FolderReceiptCanFinishAfterItsVerifiedChildrenAreMoved()
    {
        using var fixture = new Fixture();
        var childDeleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MoveSource(fixture.Source, childDeleted);
        var destination = new DelayedFolderDestination(childDeleted.Task);
        await using var engine = new TransferJobEngine(
            new TransferJobJournal(Path.Combine(fixture.Root, "jobs.sqlite"), new TestProtector()),
            location => location == source.Location ? source : destination, workers: 2);
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source.Location, destination.Location,
            TransferOperation.Move, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        await engine.CreateAsync(plan);
        await engine.RunAsync(plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = engine.Snapshots().Single();
        Assert.AreEqual(TransferJobState.Completed, snapshot.State,
            string.Join("; ", snapshot.Items.Select(item => item.Error)));
        Assert.AreEqual(1L, snapshot.CompletedFiles);
        Assert.IsTrue(Directory.Exists(fixture.Folder), "Moving selected files must retain their source directories.");
        Assert.IsFalse(File.Exists(fixture.Child));
        Assert.IsTrue(destination.FolderVerifiedAfterChildDeletion);
    }

    [TestMethod]
    public async Task SavedFolderSourceRemainsValidAfterChildDrivenTimestampChanges()
    {
        using var fixture = new Fixture();
        var entry = (await fixture.Source.DiscoverAsync()).Entries.Single();
        Assert.IsTrue(entry.IsFolder);
        File.Delete(fixture.Child);
        // Do not depend on file-system timestamp granularity to exercise this case.
        Directory.SetLastWriteTimeUtc(fixture.Folder, entry.ModifiedUtc.UtcDateTime.AddMinutes(1));
        var restarted = new LocalTransferEndpoint(fixture.Source.Location);
        await restarted.OpenSource(entry).ValidateAsync();
    }

    [TestMethod]
    public async Task FolderCreationIdentityChangeStillRejectsSavedSource()
    {
        using var fixture = new Fixture();
        var entry = (await fixture.Source.DiscoverAsync()).Entries.Single();
        Directory.SetCreationTimeUtc(fixture.Folder, Directory.GetCreationTimeUtc(fixture.Folder).AddMinutes(1));
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => fixture.Source.OpenSource(entry).ValidateAsync());
    }

    [TestMethod]
    public async Task FileTimestampChangeStillRejectsSavedSource()
    {
        using var fixture = new Fixture();
        var first = await fixture.Source.DiscoverAsync();
        var entry = (await fixture.Source.DiscoverAsync(first.NextCursor)).Entries.Single();
        File.SetLastWriteTimeUtc(fixture.Child, entry.ModifiedUtc.UtcDateTime.AddMinutes(1));
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => fixture.Source.OpenSource(entry).ValidateAsync());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudInletFolderMove-" + Guid.NewGuid().ToString("N"));
        public string Folder => Path.Combine(Root, "source", "folder");
        public string Child => Path.Combine(Folder, "child.txt");
        public LocalTransferEndpoint Source { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Child, "A verified child.");
            Source = new(LocalTransferEndpoint.ForFolder(Path.Combine(Root, "source")));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TestProtector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class MoveSource(LocalTransferEndpoint source, TaskCompletionSource childDeleted) : ITransferEndpoint
    {
        public TransferLocation Location => source.Location;
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
            source.BrowseFoldersAsync(cursor, cancellationToken);
        public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
            source.DiscoverAsync(cursor, cancellationToken);
        public ITransferSourceFile OpenSource(TransferEntry entry) => source.OpenSource(entry);
        public Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile file,
            TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile file,
            TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default)
        {
            await source.DeleteSourceAsync(entry, cancellationToken);
            var folder = Path.GetDirectoryName(Path.Combine(Location.Path, entry.RelativePath))!;
            Directory.SetLastWriteTimeUtc(folder, Directory.GetLastWriteTimeUtc(folder).AddMinutes(1));
            childDeleted.TrySetResult();
        }
    }

    private sealed class DelayedFolderDestination(Task childDeleted) : ITransferEndpoint
    {
        public TransferLocation Location { get; } = new("b2", "test-account", "test-bucket", "", "destination/", "Test destination");
        public bool FolderVerifiedAfterChildDeletion { get; private set; }
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ITransferSourceFile OpenSource(TransferEntry entry) => throw new NotSupportedException();
        public Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source,
            TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default) => Task.FromResult<TransferReceipt?>(null);
        public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source,
            TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await source.ValidateAsync(cancellationToken);
            string? digest = null;
            if (!source.Entry.IsFolder)
            {
                await using var input = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
                digest = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken));
            }
            return new("receipt-" + source.Entry.Id, request.RelativePath, "destination-version", source.Entry.Size,
                digest, request.OperationId);
        }
        public async Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
        {
            if (source.Entry.IsFolder)
            {
                await childDeleted.WaitAsync(cancellationToken);
                FolderVerifiedAfterChildDeletion = true;
            }
        }
        public Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
