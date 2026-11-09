using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ProtectedB2CloudPrefixCopyJournalTests
{
    private static B2CloudPrefixCopyCheckpoint Checkpoint() => new(1, "test-account", "test-bucket", "CloudBay/", "CloudInlet/", 1,
        [new(new string('a', 64), new("original-version", "CloudBay/private-file.txt", "upload", 7,
            new string('b', 40), 1234, "text/plain", new Dictionary<string, string> { ["private-key"] = "private-value" }),
            "CloudInlet/private-file.txt", B2CloudPrefixCopyPhase.Creating)], []);

    [TestMethod]
    public void NativeProtectedCopyIntentSurvivesReopenWithoutPlaintextOnDisk()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Open()) { journal.Write(Checkpoint()); Assert.AreEqual(1, journal.Read()!.Intents.Count); }
        using var resumed = fixture.Open();
        Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, resumed.Read()!.Intents.Single().Phase);
        var bytes = Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path));
        Assert.IsFalse(bytes.Contains("private-file")); Assert.IsFalse(bytes.Contains("private-value"));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }
    [TestMethod]
    public void NativeTamperingAndConcurrentWriterAreRejected()
    {
        using var fixture = new Fixture(); using var journal = fixture.Open(); journal.Write(Checkpoint());
        Assert.ThrowsException<IOException>(() => fixture.Open());
        var data = File.ReadAllBytes(fixture.Path); data[^1] ^= 1; File.WriteAllBytes(fixture.Path, data);
        Assert.ThrowsException<CryptographicException>(() => journal.Read());
        CollectionAssert.AreEqual(data, File.ReadAllBytes(fixture.Path));
    }
    [TestMethod]
    public void NativePlaintextIsClearedOnReadWriteAndProtectionFailure()
    {
        using var fixture = new Fixture(); byte[]? write = null, read = null;
        using (var journal = new ProtectedB2CloudPrefixCopyJournal(fixture.Path, plain => { write = plain; return Fixture.Protect(plain); },
            encrypted => read = Fixture.Unprotect(encrypted)))
        {
            journal.Write(Checkpoint()); Assert.IsTrue(write!.All(value => value == 0));
            Assert.IsNotNull(journal.Read());
            Assert.IsNull(read, "An unchanged pinned log uses its immutable snapshot without decrypting all frames again.");
        }
        using (var reopened = new ProtectedB2CloudPrefixCopyJournal(fixture.Path, Fixture.Protect,
            encrypted => read = Fixture.Unprotect(encrypted)))
        { Assert.IsNotNull(reopened.Read()); Assert.IsTrue(read!.All(value => value == 0)); }
        var original = File.ReadAllBytes(fixture.Path);
        using var failed = new ProtectedB2CloudPrefixCopyJournal(fixture.Path, plain =>
        { write = plain; throw new CryptographicException("Fixture cannot encrypt."); }, Fixture.Unprotect);
        Assert.ThrowsException<CryptographicException>(() => failed.Write(Checkpoint()));
        Assert.IsTrue(write!.All(value => value == 0)); CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.Path));
    }
    [TestMethod]
    public void NativeInvalidEnvelopeAndDirectoryCollisionCannotOverwriteRecovery()
    {
        using var fixture = new Fixture(); using (var first = fixture.Open()) first.Write(Checkpoint());
        var original = File.ReadAllBytes(fixture.Path);
        foreach (var protect in new Func<byte[], byte[]>[] { _ => [], plain => plain })
        {
            using var journal = new ProtectedB2CloudPrefixCopyJournal(fixture.Path, protect, Fixture.Unprotect);
            Assert.ThrowsException<InvalidDataException>(() => journal.Write(Checkpoint()));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.Path));
        }
        var collision = System.IO.Path.Combine(fixture.Root, "collision"); Directory.CreateDirectory(collision);
        Assert.ThrowsException<IOException>(() => new ProtectedB2CloudPrefixCopyJournal(collision, Fixture.Protect, Fixture.Unprotect));
        Assert.IsTrue(Directory.Exists(collision));
    }
    [TestMethod]
    public void NativeMultipartAndInventoryInputsCannotMutateSavedRecovery()
    {
        using var fixture = new Fixture();
        var hashes = new string?[] { new string('c', 40), null };
        var metadata = new Dictionary<string, string> { ["custom"] = "original" };
        var intents = Checkpoint().Intents.ToArray();
        intents[0] = intents[0] with { PartHashes = hashes, Source = intents[0].Source with { Metadata = metadata } };
        var hides = new[] { intents[0].Source with { FileId = "hidden", Action = "hide" } };
        var state = Checkpoint() with { Intents = intents, HideReceipts = hides };
        using (var journal = fixture.Open())
        {
            journal.Write(state);
            hashes[0] = "changed"; metadata["custom"] = "changed";
            intents[0] = intents[0] with { Phase = B2CloudPrefixCopyPhase.Canceled };
            hides[0] = hides[0] with { FileId = "changed" };
            var saved = journal.Read()!;
            Assert.AreEqual(new string('c', 40), saved.Intents[0].PartHashes![0]);
            Assert.AreEqual("original", saved.Intents[0].Source.Metadata["custom"]);
            Assert.AreEqual("hidden", saved.HideReceipts![0].FileId);
            Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, saved.Intents[0].Phase);
            journal.Write(saved with { Intents = [saved.Intents[0] with { Phase = B2CloudPrefixCopyPhase.CopyingParts }] });
        }
        using var reopened = fixture.Open();
        Assert.AreEqual(B2CloudPrefixCopyPhase.CopyingParts, reopened.Read()!.Intents[0].Phase);
        Assert.AreEqual(new string('c', 40), reopened.Read()!.Intents[0].PartHashes![0]);
    }

    [TestMethod]
    public void NativeLegacyCopySnapshotPromotesAndCorruptCommittedTailFailsClosed()
    {
        foreach (var truncate in new[] { false, true })
        {
            using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Root);
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter<B2CloudPrefixCopyPhase>(allowIntegerValues: false) } };
            File.WriteAllBytes(fixture.Path, Fixture.Protect(JsonSerializer.SerializeToUtf8Bytes(Checkpoint(), options)));
            using (var journal = fixture.Open())
            {
                var state = journal.Read()!;
                journal.Write(state with { Intents = [state.Intents[0] with { Phase = B2CloudPrefixCopyPhase.CopyingParts }] });
            }
            using (var reopened = fixture.Open()) Assert.AreEqual(B2CloudPrefixCopyPhase.CopyingParts, reopened.Read()!.Intents[0].Phase);
            var log = Directory.GetFiles(fixture.Root, "*.log").Single(); var bytes = File.ReadAllBytes(log);
            if (truncate) Array.Resize(ref bytes, bytes.Length - 1); else bytes[^1] ^= 1;
            File.WriteAllBytes(log, bytes);
            using var broken = fixture.Open();
            if (truncate) Assert.ThrowsException<InvalidDataException>(() => broken.Read());
            else Assert.ThrowsException<CryptographicException>(() => broken.Read());
            Assert.ThrowsException<InvalidDataException>(() => broken.Write(Checkpoint()));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(log));
        }
    }

    [TestMethod]
    public void NativeLargeProviderInventoryWritesBoundedDeltasAndCachesImmutableReads()
    {
        using var fixture = new Fixture(); long protectedBytes = 0; var decryptions = 0;
        var initial = Checkpoint();
        var state = initial with { Intents = Enumerable.Range(0, 16_000).Select(index => initial.Intents[0] with
        { OperationId = index.ToString("x64"), Source = initial.Intents[0].Source with { FileId = "source-" + index } }).ToArray() };
        var initialSize = JsonSerializer.SerializeToUtf8Bytes(state).Length;
        using (var journal = new ProtectedB2CloudPrefixCopyJournal(fixture.Path,
            plain => { protectedBytes += plain.Length; return Fixture.Protect(plain); },
            encrypted => { decryptions++; return Fixture.Unprotect(encrypted); }))
        {
            journal.Write(state);
            for (var index = 0; index < 128; index++)
            {
                var next = state.Intents.ToArray(); next[index] = next[index] with { Phase = B2CloudPrefixCopyPhase.CopyingParts };
                state = state with { Intents = next }; journal.Write(state); Assert.AreEqual(16_000, journal.Read()!.Intents.Count);
            }
            Assert.AreEqual(0, decryptions, "An unchanged anchor and pinned log do not replay the complete history for each progress query.");
        }
        Assert.IsTrue(protectedBytes < initialSize + 128L * 4000, $"Unexpected recovery write amplification: {protectedBytes} bytes.");
        using var reopened = fixture.Open(); var restored = reopened.Read()!;
        Assert.AreEqual(16_000, restored.Intents.Count);
        Assert.AreEqual(128, restored.Intents.Count(value => value.Phase == B2CloudPrefixCopyPhase.CopyingParts));
        Assert.AreEqual("source-15999", restored.Intents[^1].Source.FileId);
    }

    [TestMethod]
    public void NativeTenThousandPartFilePersistsOnlyChangedHashesAcrossRestart()
    {
        using var fixture = new Fixture(); long protectedBytes = 0;
        var initial = Checkpoint();
        var intent = initial.Intents[0] with { Phase = B2CloudPrefixCopyPhase.CopyingParts,
            LargeFileId = "large-copy", PartSize = 100_000_000,
            PartHashes = Enumerable.Repeat<string?>(new string('d', 40), 10_000).ToArray() };
        var state = initial with { Intents = [intent] };
        var initialSize = JsonSerializer.SerializeToUtf8Bytes(state).Length;
        using (var journal = new ProtectedB2CloudPrefixCopyJournal(fixture.Path,
            plain => { protectedBytes += plain.Length; return Fixture.Protect(plain); }, Fixture.Unprotect))
        {
            journal.Write(state);
            for (var part = 0; part < 128; part++)
            {
                var hashes = intent.PartHashes!.ToArray(); hashes[part] = new string('e', 40);
                intent = intent with { PartHashes = hashes }; state = state with { Intents = [intent] }; journal.Write(state);
            }
            journal.Write(state with { Intents = [intent with { Phase = B2CloudPrefixCopyPhase.Finishing }] });
        }
        Assert.IsTrue(protectedBytes < initialSize + 129L * 4000, $"Multipart recovery rewrote unchanged hashes: {protectedBytes} bytes.");
        using var reopened = fixture.Open(); var restored = reopened.Read()!.Intents[0];
        Assert.AreEqual(B2CloudPrefixCopyPhase.Finishing, restored.Phase);
        Assert.AreEqual(10_000, restored.PartHashes!.Count);
        Assert.AreEqual(128, restored.PartHashes.Count(value => value == new string('e', 40)));
        Assert.AreEqual(new string('d', 40), restored.PartHashes[^1]);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet-copy-journal-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "copy.dpapi");
        public ProtectedB2CloudPrefixCopyJournal Open() => new(Path, Protect, Unprotect);
        public static byte[] Protect(byte[] bytes) => ProtectedData.Protect(bytes, "Fixture.B2Copy"u8.ToArray(), DataProtectionScope.CurrentUser);
        public static byte[] Unprotect(byte[] bytes) => ProtectedData.Unprotect(bytes, "Fixture.B2Copy"u8.ToArray(), DataProtectionScope.CurrentUser);
        public void Dispose()
        {
            var absolute = System.IO.Path.GetFullPath(Root);
            if (System.IO.Path.GetDirectoryName(absolute) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()) ||
                !System.IO.Path.GetFileName(absolute).StartsWith("CloudInlet-copy-journal-", StringComparison.Ordinal)) throw new IOException("Invalid fixture cleanup path.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
