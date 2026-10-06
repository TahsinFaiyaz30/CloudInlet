using System.Security.Cryptography;
using CloudBay.Core;
using CloudBay.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class LocalTransferReplacementTests
{
    [TestMethod]
    public async Task KeepBothRetainsTheOriginalNameWhenNoConflictExists()
    {
        using var fixture = new Fixture(TransferConflictPolicy.Rename, originalExists: false);
        var receipt = await fixture.UploadAsync();
        Assert.AreEqual("item.bin", receipt.RelativePath);
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
        Assert.AreEqual(1, Directory.GetFiles(fixture.DirectoryPath).Length);
    }

    [TestMethod]
    public async Task KeepBothRenamesOnlyTheConflictingCopyAndRestoresItsSelectedTarget()
    {
        using var fixture = new Fixture(TransferConflictPolicy.Rename);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") == "true") throw new OperationCanceledException();
        }));
        var targetName = "item (CloudBay " + fixture.Request.OperationId[..12] + ").bin";
        Assert.AreEqual(targetName, fixture.Saved!.Data!["relativePath"]);
        Assert.AreEqual("item.bin", fixture.Saved.Data["requestedPath"]);
        var restarted = new LocalTransferEndpoint(LocalTransferEndpoint.ForFolder(fixture.DirectoryPath));
        var receipt = await restarted.UploadAsync(fixture.Request, fixture.Source, fixture.Saved, (_, _) => Task.CompletedTask);
        await restarted.VerifyAsync(receipt, fixture.Source);
        Assert.AreEqual(targetName, receipt.RelativePath);
        Assert.AreEqual(1, fixture.Source.Reads, "The persisted selected name and completed ranges survive restart.");
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, targetName)));
        var recovered = await restarted.ReconcileAsync(fixture.Request, fixture.Source, fixture.Saved);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(receipt.Id, recovered.Id);
    }

    [TestMethod]
    public async Task KeepBothNeverOverwritesALateFileAtTheSavedAlternateName()
    {
        using var fixture = new Fixture(TransferConflictPolicy.Rename);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") == "true") throw new OperationCanceledException();
        }));
        var renamedPath = Path.Combine(fixture.DirectoryPath, fixture.Saved!.Data!["relativePath"]);
        var late = new byte[] { 1, 4, 7 };
        File.WriteAllBytes(renamedPath, late);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.UploadAsync(fixture.Saved));
        CollectionAssert.AreEqual(late, File.ReadAllBytes(renamedPath));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Final));
    }

    [TestMethod]
    public async Task KeepBothRejectsAnUnrelatedSavedTarget()
    {
        using var fixture = new Fixture(TransferConflictPolicy.Rename);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.UploadAsync(onCheckpoint: checkpoint => throw new OperationCanceledException()));
        var data = new Dictionary<string, string>(fixture.Saved!.Data!) { ["relativePath"] = "another.bin" };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.UploadAsync(fixture.Saved with { Data = data }));
        Assert.AreEqual(0, fixture.Source.Reads);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DirectoryPath, "another.bin")));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Final));
    }

    [TestMethod]
    public async Task ReplacementPreservesTheExactOriginalWithoutOverwrite()
    {
        using var fixture = new Fixture();
        var receipt = await fixture.UploadAsync();
        Assert.IsTrue(File.Exists(fixture.Recovery), "After commit: " + string.Join(", ", Directory.GetFiles(fixture.DirectoryPath)));
        await fixture.Endpoint.VerifyAsync(receipt, fixture.Source);
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Recovery));
        Assert.AreEqual(Path.GetFileName(fixture.Recovery), receipt.Data!["recoveryRelativePath"]);
    }

    [TestMethod]
    public async Task SameMetadataReplacementAtTheFinalPathIsNeverOverwritten()
    {
        using var fixture = new Fixture();
        var creation = File.GetCreationTimeUtc(fixture.Final);
        var modified = File.GetLastWriteTimeUtc(fixture.Final);
        var moved = Path.Combine(fixture.DirectoryPath, "original-retained.bin");
        var replacement = new byte[fixture.Original.Length];
        RandomNumberGenerator.Fill(replacement);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") != "true") return;
            File.Move(fixture.Final, moved);
            File.WriteAllBytes(fixture.Final, replacement);
            File.SetCreationTimeUtc(fixture.Final, creation);
            File.SetLastWriteTimeUtc(fixture.Final, modified);
        }));
        CollectionAssert.AreEqual(replacement, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(moved));
        Assert.IsFalse(File.Exists(fixture.Recovery));
    }

    [TestMethod]
    public async Task RestartBetweenExactPreservationAndInstallationDoesNotRereadSourcePayload()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") == "true") throw new OperationCanceledException();
        }));
        File.Move(fixture.Final, fixture.Recovery);
        var receipt = await fixture.UploadAsync(fixture.Saved);
        await fixture.Endpoint.VerifyAsync(receipt, fixture.Source);
        Assert.AreEqual(1, fixture.Source.Reads, "Acknowledged local ranges survive restart and are verified without retransferring the source.");
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Recovery));
    }

    [TestMethod]
    public async Task NewDestinationAfterPreservationRetainsEveryFile()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") == "true") throw new OperationCanceledException();
        }));
        File.Move(fixture.Final, fixture.Recovery);
        var late = new byte[] { 9, 8, 7 };
        File.WriteAllBytes(fixture.Final, late);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.UploadAsync(fixture.Saved));
        CollectionAssert.AreEqual(late, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Recovery));
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Partial));
    }

    [TestMethod]
    public async Task ForeignFileAtRecoveryPathIsNeverOverwritten()
    {
        using var fixture = new Fixture();
        var foreign = new byte[] { 4, 3, 2, 1 };
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") == "true") File.WriteAllBytes(fixture.Recovery, foreign);
        }));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(foreign, File.ReadAllBytes(fixture.Recovery));
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Partial));
    }

    [TestMethod]
    public async Task LateLinkToTheOriginalFileCannotMoveItsExternalTarget()
    {
        using var fixture = new Fixture();
        var moved = Path.Combine(fixture.DirectoryPath, "outside-original.bin");
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data?.GetValueOrDefault("committing") != "true") return;
            File.Move(fixture.Final, moved);
            File.CreateSymbolicLink(fixture.Final, moved);
        }));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(moved));
        Assert.IsNotNull(new FileInfo(fixture.Final).LinkTarget);
        Assert.IsFalse(File.Exists(fixture.Recovery));
    }

    [TestMethod]
    public async Task RecoveryNeverAcknowledgesAReplacedPreservedOriginal()
    {
        using var fixture = new Fixture();
        await fixture.UploadAsync();
        var checkpoint = fixture.Saved!;
        var creation = File.GetCreationTimeUtc(fixture.Recovery);
        var modified = File.GetLastWriteTimeUtc(fixture.Recovery);
        File.Move(fixture.Recovery, fixture.Recovery + ".retained");
        File.WriteAllBytes(fixture.Recovery, fixture.Original);
        File.SetCreationTimeUtc(fixture.Recovery, creation);
        File.SetLastWriteTimeUtc(fixture.Recovery, modified);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.Endpoint.ReconcileAsync(fixture.Request, fixture.Source, checkpoint));
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Recovery + ".retained"));
        CollectionAssert.AreEqual(fixture.Original, File.ReadAllBytes(fixture.Recovery));
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CloudBayReplacement-" + Guid.NewGuid().ToString("N"));
        public byte[] Original { get; } = "original"u8.ToArray();
        public byte[] Payload { get; } = "new-copy"u8.ToArray();
        public LocalTransferEndpoint Endpoint { get; }
        public MemorySource Source { get; }
        public TransferUploadRequest Request { get; }
        public string Final => Path.Combine(DirectoryPath, "item.bin");
        public string Recovery => Path.Combine(DirectoryPath, ".CloudBay-transfer-" + Request.OperationId + ".original");
        public string Partial => Path.Combine(DirectoryPath, ".CloudBay-transfer-" + Request.OperationId + ".part");
        public TransferCheckpoint? Saved { get; private set; }
        public Fixture(TransferConflictPolicy policy = TransferConflictPolicy.Replace, bool originalExists = true)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (originalExists) File.WriteAllBytes(Final, Original);
            Endpoint = new(LocalTransferEndpoint.ForFolder(DirectoryPath));
            Request = new(Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(), "item.bin", policy);
            Source = new(Payload);
        }
        public Task<TransferReceipt> UploadAsync(TransferCheckpoint? checkpoint = null, Action<TransferCheckpoint>? onCheckpoint = null) =>
            Endpoint.UploadAsync(Request, Source, checkpoint, (value, _) =>
            {
                Saved = value;
                onCheckpoint?.Invoke(value);
                return Task.CompletedTask;
            });
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class MemorySource(byte[] payload) : ITransferSourceFile
    {
        public int Reads { get; private set; }
        public TransferEntry Entry { get; } = new("memory-source", "item.bin", "source-v1", payload.Length, DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant());
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<Stream>(new MemoryStream(payload, (int)offset, (int)length, writable: false));
        }
        public Task ValidateAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
