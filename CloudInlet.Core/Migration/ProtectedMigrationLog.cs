using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace CloudInlet.Core.Migration;

/// <summary>
/// Authenticated, append-only metadata deltas with a small atomic committed-length anchor. A torn
/// append is never committed; a missing or altered committed frame is an error. The caller holds
/// native ancestor guards. This class holds the writer lease and the exact opened log file.
/// </summary>
internal sealed class ProtectedMigrationLog<T, TDelta> : IDisposable where T : class where TDelta : class
{
    private const string Format = "CloudInlet.ProtectedMigrationLog.v1";
    private const int MaximumEnvelopeBytes = 64 * 1024 * 1024, MaximumFrames = 1_000_000;
    private const long MaximumLogBytes = 2L * 1024 * 1024 * 1024;
    private sealed record Anchor(string Format, string Kind, string Id, long Length, int Sequence, string Hash);
    private sealed record Frame(string Kind, string Id, int Sequence, string? PreviousHash, TDelta Delta);
    private readonly string _path, _kind;
    private readonly Func<byte[], byte[]> _protect, _unprotect;
    private readonly Func<T?, T, TDelta> _delta;
    private readonly Func<IEnumerable<TDelta>, T> _restore;
    private readonly JsonSerializerOptions _options;
    private readonly FileStream _lease;
    private FileStream? _log;
    private Anchor? _anchor;
    private string? _anchorHash;
    private T? _value;
    private bool _loaded, _disposed, _failed;

    public ProtectedMigrationLog(string path, string kind, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect,
        JsonSerializerOptions options, Func<T?, T, TDelta> delta, Func<IEnumerable<TDelta>, T> restore)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute recovery path is required.", nameof(path));
        _path = Path.GetFullPath(path); _kind = kind;
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
        _options = options; _delta = delta; _restore = restore;
        ValidatePath(_path); Directory.CreateDirectory(Path.GetDirectoryName(_path)!); ValidatePath(_path);
        ValidatePath(_path + ".lock");
        _lease = new(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public T? Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw Invalid("Reopen the authenticated recovery log after its previous failure.");
        byte[]? plain = null;
        try
        {
            var bytes = ReadAnchorBytes();
            if (bytes is null)
            {
                if (_loaded && _value is not null) throw Invalid("The committed recovery anchor disappeared while its writer lease was held.");
                _value = null; _anchorHash = null; _loaded = true; return null;
            }
            var hash = Hash(bytes);
            // The exact log remains open without write/delete sharing throughout this lease.
            // Re-reading its unchanged authenticated anchor therefore does not need to replay
            // the complete immutable inventory on every UI progress/revertability query.
            if (_loaded && _value is not null && hash == _anchorHash && (_log is not null || _anchor is null)) return _value;
            plain = _unprotect(bytes);
            if (_loaded && _value is not null && hash != _anchorHash)
                throw Invalid("The recovery anchor changed while its writer lease was held.");
            if (plain.Length is <= 0 or > MaximumEnvelopeBytes) throw Invalid("The recovery record is empty or too large.");
            using var document = JsonDocument.Parse(plain);
            if (!document.RootElement.TryGetProperty("Format", out var format))
            {
                // Existing protected full checkpoints remain readable and are promoted atomically
                // on the next successful write. Published recovery records are never discarded.
                _value = JsonSerializer.Deserialize<T>(plain, _options) ?? throw Invalid("The recovery record is empty.");
            }
            else
            {
                if (format.GetString() != Format) throw Invalid("The recovery log format is not supported.");
                _anchor = JsonSerializer.Deserialize<Anchor>(plain, _options) ?? throw Invalid("The recovery anchor is empty.");
                if (_anchor.Kind != _kind || !Guid.TryParseExact(_anchor.Id, "N", out _) ||
                    _anchor.Length is <= 4 or > MaximumLogBytes || _anchor.Sequence is < 1 or > MaximumFrames ||
                    _anchor.Hash is not { Length: 64 } || !_anchor.Hash.All(Uri.IsHexDigit))
                    throw Invalid("The recovery anchor is inconsistent.");
                var logPath = LogPath(_anchor.Id); ValidatePath(logPath);
                _log = new(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (_log.Length < _anchor.Length) throw Invalid("Committed recovery metadata was truncated. No progress was guessed.");
                _value = _restore(ReadFrames(_anchor));
            }
            _anchorHash = hash; _loaded = true; return _value;
        }
        catch { _log?.Dispose(); _log = null; _failed = true; throw; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Write(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw Invalid("Reopen the authenticated recovery log after its previous failure.");
        if (!_loaded) Read();
        var current = ReadAnchorBytes();
        if ((current is null ? null : Hash(current)) != _anchorHash)
            throw Invalid("The recovery anchor changed while its writer lease was held.");
        try
        {
            var first = _anchor is null;
            var id = _anchor?.Id ?? Guid.NewGuid().ToString("N");
            var sequence = checked((_anchor?.Sequence ?? 0) + 1);
            if (sequence > MaximumFrames) throw Invalid("The recovery log reached its metadata limit.");
            var difference = _delta(_value, value);
            // A legacy full checkpoint has no log yet, but must obey exactly the same identity
            // and append-only receipt rules before its atomic promotion to a new first frame.
            var frame = new Frame(_kind, id, sequence, _anchor?.Hash, first && _value is not null ? _delta(null, value) : difference);
            var bytes = Protect(frame);
            var length = checked((_anchor?.Length ?? 0) + 4L + bytes.Length);
            if (length > MaximumLogBytes) throw Invalid("The recovery log reached its metadata limit.");
            var next = new Anchor(Format, _kind, id, length, sequence, Hash(bytes));
            // Protect both records before changing either file, so encryption failure preserves
            // the previous committed anchor and leaves no newly accepted state.
            var anchorBytes = Protect(next);
            if (first)
            {
                var logPath = LogPath(id); ValidatePath(logPath);
                _log = new(logPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            }
            // Any trailing bytes belong to a write whose atomic anchor never committed. They are
            // removed only under the exact authenticated anchor and exclusive open-file lease.
            _log!.SetLength(_anchor?.Length ?? 0); _log.Position = _log.Length;
            Span<byte> header = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            _log.Write(header); _log.Write(bytes); _log.Flush(true);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidatePath(temporary);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(anchorBytes); output.Flush(true); }
                ValidatePath(_path); File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _anchor = next; _anchorHash = Hash(anchorBytes); _value = value;
        }
        catch { _failed = true; throw; }
    }

    private IEnumerable<TDelta> ReadFrames(Anchor anchor)
    {
        _log!.Position = 0; string? previous = null;
        var header = new byte[4];
        for (var sequence = 1; sequence <= anchor.Sequence; sequence++)
        {
            if (anchor.Length - _log.Position < 4) throw Invalid("A committed recovery frame is missing.");
            _log.ReadExactly(header); var count = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (count is <= 0 or > MaximumEnvelopeBytes || count > anchor.Length - _log.Position)
                throw Invalid("A committed recovery frame is truncated or too large.");
            var bytes = new byte[count]; _log.ReadExactly(bytes);
            var plain = _unprotect(bytes); TDelta delta;
            try
            {
                if (plain.Length is <= 0 or > MaximumEnvelopeBytes) throw Invalid("A recovery frame exceeds its metadata limit.");
                var frame = JsonSerializer.Deserialize<Frame>(plain, _options) ?? throw Invalid("A recovery frame is empty.");
                if (frame.Kind != _kind || frame.Id != anchor.Id || frame.Sequence != sequence || frame.PreviousHash != previous || frame.Delta is null)
                    throw Invalid("Recovery frame ownership or order does not match its committed anchor.");
                delta = frame.Delta;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            previous = Hash(bytes); yield return delta;
        }
        if (_log.Position != anchor.Length || previous != anchor.Hash)
            throw Invalid("The committed recovery chain does not match its anchor.");
    }

    private byte[] Protect<TValue>(TValue value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        try
        {
            if (plain.Length is <= 0 or > MaximumEnvelopeBytes) throw Invalid("Recovery metadata is empty or too large.");
            var bytes = _protect(plain);
            if (ReferenceEquals(bytes, plain) || bytes.Length is <= 0 or > MaximumEnvelopeBytes)
                throw Invalid("Protection must return a separate, nonempty authenticated envelope within the metadata limit.");
            return bytes;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private byte[]? ReadAnchorBytes()
    {
        ValidatePath(_path);
        if (!File.Exists(_path)) return null;
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumEnvelopeBytes) throw Invalid("The recovery anchor is empty or too large.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }
    private string LogPath(string id) => _path + "." + id + ".log";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidDataException Invalid(string message) => new(message);
    private static void ValidatePath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || current == path && (attributes & FileAttributes.Directory) != 0)
                    throw new IOException("Cloud recovery metadata cannot follow linked or unexpected paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _log?.Dispose(); _lease.Dispose();
    }
}
