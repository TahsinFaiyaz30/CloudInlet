using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;

namespace CloudBay.Application;

public sealed class ClientStorage
{
    public sealed record BackupIntent(BackupFolder Folder, bool Enable);
    public string DirectoryPath { get; }
    public string DiagnosticsPath => Path.Combine(DirectoryPath, "activity.jsonl");
    private readonly object _logGate = new();
    private readonly List<ActivityEvent> _activity = [];
    private readonly Dictionary<string, ActivityEvent> _lastErrors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public ClientStorage(string? path = null)
    {
        DirectoryPath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Client");
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(DiagnosticsPath))
        {
            foreach (var line in File.ReadLines(DiagnosticsPath).TakeLast(300))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ActivityEvent>(line) is { } value)
                    { _activity.Add(value); TrackError(value); }
                }
                catch (JsonException) { /* An interrupted final line does not invalidate earlier events. */ }
            }
        }
    }
    public AppSettings LoadSettings()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("CloudBay settings could not be read. The original file was preserved.");
        if (result.SchemaVersion != 1) throw new InvalidDataException("These settings were created by a newer CloudBay release.");
        return result;
    }
    public void SaveSettings(AppSettings settings) => AtomicWrite(Path.Combine(DirectoryPath, "settings.json"),
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonOptions)));
    public void SaveBackupIntent(BackupFolder folder, bool enable) => AtomicWrite(Path.Combine(DirectoryPath, "backup-pending.json"),
        JsonSerializer.SerializeToUtf8Bytes(new BackupIntent(folder, enable)));
    public BackupIntent? LoadBackupIntent()
    {
        var path = Path.Combine(DirectoryPath, "backup-pending.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<BackupIntent>(File.ReadAllText(path)) : null;
    }
    public void ClearBackupIntent() => File.Delete(Path.Combine(DirectoryPath, "backup-pending.json"));
    public void ClearCredentials()
    {
        File.Delete(Path.Combine(DirectoryPath, "credentials.dpapi"));
        File.Delete(Path.Combine(DirectoryPath, "credentials.dpapi.bak"));
    }
    public void SaveCredentials(B2Credentials credentials)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var protectedBytes = ProtectedData.Protect(plaintext, "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            AtomicWrite(Path.Combine(DirectoryPath, "credentials.dpapi"), protectedBytes);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public B2Credentials? LoadCredentials()
    {
        var path = Path.Combine(DirectoryPath, "credentials.dpapi");
        if (!File.Exists(path)) return null;
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<B2Credentials>(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public IReadOnlyList<ActivityEvent> Activity { get { lock (_logGate) return _activity.AsEnumerable().Reverse().ToArray(); } }
    public bool Log(ActivityEvent value)
    {
        lock (_logGate)
        {
            // A failed file remains visible in Attention, but a polling retry must not bury
            // the rest of history beneath identical errors. Changed errors and recovery
            // are recorded immediately; an ongoing issue gets a reminder every five minutes.
            if (value.Kind == ActivityKind.Error && _lastErrors.TryGetValue(value.Path, out var previous) &&
                value.Message == previous.Message && value.Time >= previous.Time && value.Time - previous.Time < TimeSpan.FromMinutes(5))
                return false;
            TrackError(value);
            _activity.Add(value);
            if (_activity.Count > 300) _activity.RemoveAt(0);
            try
            {
                if (File.Exists(DiagnosticsPath) && new FileInfo(DiagnosticsPath).Length > 5 * 1024 * 1024)
                    File.Move(DiagnosticsPath, DiagnosticsPath + ".previous", overwrite: true);
                File.AppendAllText(DiagnosticsPath, JsonSerializer.Serialize(value) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* Logging failure must never lose a completed transfer. */ }
            return true;
        }
    }
    private void TrackError(ActivityEvent value)
    {
        if (value.Kind != ActivityKind.Error) { if (value.Completed) _lastErrors.Remove(value.Path); return; }
        if (!_lastErrors.ContainsKey(value.Path) && _lastErrors.Count >= 2048)
            _lastErrors.Remove(_lastErrors.MinBy(pair => pair.Value.Time).Key);
        _lastErrors[value.Path] = value;
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
