using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ProtectedCloudPrefixMigrationJournalTests
{
    private static CloudPrefixMigrationCheckpoint Checkpoint() => new(1, new string('a', 64), "cloud", "fixture-account", "fixture-bucket",
        "CloudBay/", "CloudInlet/", new string('b', 64), CloudPrefixMigrationPhase.Copying,
        [new(new("source-id", "CloudBay/private-name.txt", "upload", 3, new string('a', 40), 1234, "text/plain",
            new Dictionary<string, string> { ["custom"] = "private-value" }), "CloudInlet/private-name.txt", new string('c', 64))]);

    [TestMethod]
    public void AuthenticatedReceiptRoundTripsAcrossRestartWithoutPlaintextFile()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Open())
        {
            Assert.IsNull(journal.Read());
            journal.Write(Checkpoint());
            Assert.AreEqual("private-value", journal.Read()!.Entries.Single().Source.Metadata["custom"]);
        }
        using var reopened = fixture.Open();
        Assert.AreEqual("fixture-account", reopened.Read()!.AccountId);
        var disk = Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path));
        Assert.IsFalse(disk.Contains("private-name", StringComparison.Ordinal));
        Assert.IsFalse(disk.Contains("private-value", StringComparison.Ordinal));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }

    [TestMethod]
    public void ExclusiveJournalLeaseRejectsConcurrentWriterAndReleasesOnDispose()
    {
        using var fixture = new Fixture();
        using (var first = fixture.Open())
            Assert.ThrowsException<IOException>(() => fixture.Open());
        using var reopened = fixture.Open();
        reopened.Write(Checkpoint());
        Assert.IsNotNull(reopened.Read());
    }

    [TestMethod]
    public void TamperingCannotDeserializeOrOverwriteExistingRecoveryRecord()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Open();
        journal.Write(Checkpoint());
        var corrupted = File.ReadAllBytes(fixture.Path);
        corrupted[^1] ^= 1;
        File.WriteAllBytes(fixture.Path, corrupted);
        Assert.ThrowsException<AuthenticationTagMismatchException>(() => journal.Read());
        CollectionAssert.AreEqual(corrupted, File.ReadAllBytes(fixture.Path));
    }

    [TestMethod]
    public void PlaintextBuffersAreClearedAfterSuccessfulSerializationAndRead()
    {
        using var fixture = new Fixture();
        byte[]? writtenPlain = null, readPlain = null;
        using (var journal = new ProtectedCloudPrefixMigrationJournal(fixture.Path,
            plain => { writtenPlain = plain; return fixture.Protect(plain); },
            fixture.Unprotect))
        {
            journal.Write(Checkpoint());
            Assert.IsTrue(writtenPlain!.All(value => value == 0));
        }
        using var reopened = new ProtectedCloudPrefixMigrationJournal(fixture.Path, fixture.Protect,
            envelope => readPlain = fixture.Unprotect(envelope));
        Assert.AreEqual("fixture-account", reopened.Read()!.AccountId);
        Assert.IsTrue(readPlain!.All(value => value == 0));
    }

    [TestMethod]
    public void ProtectionFailureClearsPlaintextAndPreservesLastDurableReceipt()
    {
        using var fixture = new Fixture();
        using (var original = fixture.Open()) original.Write(Checkpoint());
        var before = File.ReadAllBytes(fixture.Path);
        byte[]? captured = null;
        using var journal = new ProtectedCloudPrefixMigrationJournal(fixture.Path,
            plain => { captured = plain; throw new CryptographicException("Injected encryption failure"); }, fixture.Unprotect);
        Assert.ThrowsException<CryptographicException>(() => journal.Write(Checkpoint() with { Phase = CloudPrefixMigrationPhase.Switching }));
        Assert.IsTrue(captured!.All(value => value == 0));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Path));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }

    [TestMethod]
    public void MalformedAuthenticatedJsonFailsClosedAndClearsPlaintext()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Path, fixture.Protect(Encoding.UTF8.GetBytes("{\"unexpected\":true}")));
        byte[]? captured = null;
        using var journal = new ProtectedCloudPrefixMigrationJournal(fixture.Path, fixture.Protect,
            bytes => captured = fixture.Unprotect(bytes));
        Assert.ThrowsException<JsonException>(() => journal.Read());
        Assert.IsTrue(captured!.All(value => value == 0));
    }

    [TestMethod]
    public void EmptyProtectionOrAliasedPlaintextCannotReplaceDurableReceipt()
    {
        using var fixture = new Fixture();
        using (var original = fixture.Open()) original.Write(Checkpoint());
        var before = File.ReadAllBytes(fixture.Path);
        foreach (var protect in new Func<byte[], byte[]>[] { _ => [], plain => plain })
        {
            using var journal = new ProtectedCloudPrefixMigrationJournal(fixture.Path, protect, fixture.Unprotect);
            Assert.ThrowsException<InvalidDataException>(() => journal.Write(Checkpoint()));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Path));
        }
    }

    [TestMethod]
    public void DirectoryInPlaceOfReceiptOrLeaseIsRejectedWithoutChangingIt()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path);
        Assert.ThrowsException<IOException>(() => fixture.Open());
        Directory.Delete(fixture.Path);
        Directory.CreateDirectory(fixture.Path + ".lock");
        Assert.ThrowsException<IOException>(() => fixture.Open());
        Assert.IsTrue(Directory.Exists(fixture.Path + ".lock"));
    }

    [TestMethod]
    public void DisposedJournalRejectsEveryReadAndWrite()
    {
        using var fixture = new Fixture();
        var journal = fixture.Open();
        journal.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => journal.Read());
        Assert.ThrowsException<ObjectDisposedException>(() => journal.Write(Checkpoint()));
    }

    [TestMethod]
    public void LegacyProtectedSnapshotPromotesWithoutLosingOriginalReceipts()
    {
        using var fixture = new Fixture();
        var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter<CloudPrefixMigrationPhase>(allowIntegerValues: false) } };
        File.WriteAllBytes(fixture.Path, fixture.Protect(JsonSerializer.SerializeToUtf8Bytes(Checkpoint(), options)));
        using (var journal = fixture.Open())
        {
            var previous = journal.Read()!;
            Assert.AreEqual("source-id", previous.Entries.Single().Source.FileId);
            journal.Write(previous with { Phase = CloudPrefixMigrationPhase.Switching });
        }
        using var reopened = fixture.Open();
        Assert.AreEqual(CloudPrefixMigrationPhase.Switching, reopened.Read()!.Phase);
        Assert.AreEqual(1, Directory.GetFiles(fixture.Root, "*.log").Length);
    }

    [TestMethod]
    public void CommittedDeltaTruncationAndCorruptionCannotRevertToAnOlderSnapshot()
    {
        foreach (var truncate in new[] { false, true })
        {
            using var fixture = new Fixture();
            using (var journal = fixture.Open())
            {
                journal.Write(Checkpoint());
                journal.Write(Checkpoint() with { Phase = CloudPrefixMigrationPhase.Switching });
            }
            var log = Directory.GetFiles(fixture.Root, "*.log").Single(); var bytes = File.ReadAllBytes(log);
            if (truncate) Array.Resize(ref bytes, bytes.Length - 1); else bytes[^1] ^= 1;
            File.WriteAllBytes(log, bytes);
            using var reopened = fixture.Open();
            if (truncate) Assert.ThrowsException<InvalidDataException>(() => reopened.Read());
            else Assert.ThrowsException<AuthenticationTagMismatchException>(() => reopened.Read());
            Assert.ThrowsException<InvalidDataException>(() => reopened.Write(Checkpoint()));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(log));
        }
    }

    [TestMethod]
    public void IncompleteUncommittedTailIsIgnoredAndRemovedOnlyByTheAuthenticatedWriter()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Open()) journal.Write(Checkpoint());
        var log = Directory.GetFiles(fixture.Root, "*.log").Single(); var committedLength = new FileInfo(log).Length;
        using (var append = new FileStream(log, FileMode.Append)) append.Write(new byte[10_000]);
        using (var reopened = fixture.Open())
        {
            var checkpoint = reopened.Read()!;
            Assert.AreEqual(CloudPrefixMigrationPhase.Copying, checkpoint.Phase);
            Assert.AreEqual(committedLength + 10_000, new FileInfo(log).Length, "Reading never silently edits recovery state.");
            reopened.Write(checkpoint with { Phase = CloudPrefixMigrationPhase.Switching });
            Assert.IsTrue(new FileInfo(log).Length < committedLength + 10_000);
        }
        using var final = fixture.Open(); Assert.AreEqual(CloudPrefixMigrationPhase.Switching, final.Read()!.Phase);
    }

    [TestMethod]
    public void OpenLogCannotBeChangedOrExchangedWhileRecoveryWriterOwnsIt()
    {
        using var fixture = new Fixture(); using var journal = fixture.Open(); journal.Write(Checkpoint());
        var log = Directory.GetFiles(fixture.Root, "*.log").Single();
        Assert.ThrowsException<IOException>(() => File.WriteAllText(log, "replacement"));
        Assert.ThrowsException<IOException>(() => File.Move(log, log + ".moved"));
        Assert.IsNotNull(journal.Read());
    }

    [TestMethod]
    public void LargeInventoryWritesOnlyChangedVersionMetadataAndRestoresLatestProgress()
    {
        using var fixture = new Fixture(); long protectedBytes = 0;
        var initial = Checkpoint();
        var entries = Enumerable.Range(0, 16_000).Select(index => initial.Entries[0] with
        {
            Source = initial.Entries[0].Source with { FileId = "source-" + index, Key = "CloudBay/file-" + index + ".txt" },
            DestinationKey = "CloudInlet/file-" + index + ".txt", OperationId = index.ToString("x64")
        }).ToArray();
        var state = initial with { Entries = entries };
        var initialSize = JsonSerializer.SerializeToUtf8Bytes(state).Length;
        using (var journal = new ProtectedCloudPrefixMigrationJournal(fixture.Path,
            plain => { protectedBytes += plain.Length; return fixture.Protect(plain); }, fixture.Unprotect))
        {
            journal.Write(state);
            for (var index = 0; index < 128; index++)
            {
                var next = state.Entries.ToArray(); next[index] = next[index] with { RequestPending = true };
                state = state with { Entries = next }; journal.Write(state);
            }
        }
        Assert.IsTrue(protectedBytes < initialSize + 128L * 4000, $"Metadata write amplification: {protectedBytes} bytes for {initialSize} initial bytes.");
        using var reopened = fixture.Open(); var restored = reopened.Read()!;
        Assert.AreEqual(16_000, restored.Entries.Count); Assert.AreEqual(128, restored.Entries.Count(value => value.RequestPending));
        Assert.AreEqual("source-15999", restored.Entries[^1].Source.FileId);
    }

    [TestMethod]
    public void CallerMutationCannotChangeTheCachedOrDurableInventory()
    {
        using var fixture = new Fixture();
        var metadata = new Dictionary<string, string> { ["custom"] = "original" };
        var entries = Checkpoint().Entries.ToArray();
        entries[0] = entries[0] with { Source = entries[0].Source with { Metadata = metadata } };
        var state = Checkpoint() with { Entries = entries };
        using (var journal = fixture.Open())
        {
            journal.Write(state);
            metadata["custom"] = "mutated";
            entries[0] = entries[0] with { Removed = true };
            var cached = journal.Read()!;
            Assert.IsFalse(cached.Entries[0].Removed);
            Assert.AreEqual("original", cached.Entries[0].Source.Metadata["custom"]);
            // A subsequent phase write must not accidentally authenticate edits
            // made through aliases to the original caller-owned input buffers.
            journal.Write(cached with { Phase = CloudPrefixMigrationPhase.Switching });
        }
        using var reopened = fixture.Open();
        Assert.IsFalse(reopened.Read()!.Entries[0].Removed);
        Assert.AreEqual("original", reopened.Read()!.Entries[0].Source.Metadata["custom"]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet-CloudJournalTests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "receipt.dpapi");
        public Fixture() => Directory.CreateDirectory(Root);
        public ProtectedCloudPrefixMigrationJournal Open() => new(Path, Protect, Unprotect);
        public byte[] Protect(byte[] plain)
        {
            var bytes = new byte[28 + plain.Length];
            RandomNumberGenerator.Fill(bytes.AsSpan(0, 12));
            using var cipher = new AesGcm(_key, 16);
            cipher.Encrypt(bytes.AsSpan(0, 12), plain, bytes.AsSpan(28), bytes.AsSpan(12, 16));
            return bytes;
        }
        public byte[] Unprotect(byte[] bytes)
        {
            var plain = new byte[bytes.Length - 28];
            using var cipher = new AesGcm(_key, 16);
            cipher.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            return plain;
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_key);
            Directory.Delete(Root, true);
        }
    }
}
