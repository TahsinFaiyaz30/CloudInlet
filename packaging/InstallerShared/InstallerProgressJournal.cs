using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Packaging;

/// <summary>Observer-only installer evidence. This journal never authorizes resuming an installer.</summary>
[DataContract]
internal sealed class InstallerProgressRecord
{
    [DataMember(IsRequired = true)] public int SchemaVersion { get; set; }
    [DataMember(IsRequired = true)] public string Id { get; set; } = "";
    [DataMember(IsRequired = true)] public string RequestHash { get; set; } = "";
    [DataMember(IsRequired = true)] public long Sequence { get; set; }
    [DataMember(IsRequired = true)] public string Stage { get; set; } = "";
    [DataMember(IsRequired = true)] public string Detail { get; set; } = "";
    [DataMember(IsRequired = true)] public string Status { get; set; } = "";
    [DataMember(IsRequired = true)] public string UpdatedUtc { get; set; } = "";
    [DataMember(IsRequired = true)] public bool MigrationRequested { get; set; }
    [DataMember(IsRequired = true)] public bool MigrationCompleted { get; set; }
    [DataMember(IsRequired = true)] public bool RequiresRestart { get; set; }
    [DataMember] public bool RecoveryWindowShown { get; set; }
}

internal sealed class InstallerProgressJournal : IDisposable
{
    private const int MaximumBytes = 16 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudInlet.InstallerProgress.v1");
    private static readonly string[] Stages = { "Validating", "WaitingForAppExit", "VerifyingPackage", "PackageVerified", "InstallerRunning", "InstallerBusy", "VerifyingInstallation", "Complete", "RestartRequired", "Failed" };
    private readonly string _path, _id, _requestHash;
    private readonly List<SafeFileHandle> _guards;
    private readonly FileStream _lease;
    private InstallerProgressRecord? _last;
    private bool _disposed;

    private InstallerProgressJournal(string path, string id, string requestHash)
    {
        ValidateIdentity(id, requestHash);
        _path = NormalPath(path); _id = id; _requestHash = requestHash;
        _guards = GuardParents(_path);
        try
        {
            UpdateCacheLocation.EnsureNormalPath(_path + ".lock", false);
            _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                UpdateCacheLocation.EnsureNormalPath(_path, false);
                if (File.Exists(_path)) throw new IOException("This update already has progress evidence. Review its result before starting a new update request.");
            }
            catch { _lease.Dispose(); throw; }
        }
        catch { DisposeGuards(_guards); throw; }
    }

    internal static InstallerProgressJournal Create(string path, string id, string requestHash) => new(path, id, requestHash);

    internal static string RequestHash(byte[] requestBytes)
    {
        using var algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(requestBytes)).Replace("-", "").ToLowerInvariant();
    }

    internal static string ValidatePath(string cache, string path, string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new IOException("The installer progress identity is invalid.");
        var expected = Path.Combine(NormalPath(cache), "host", id + ".progress.dpapi");
        if (!string.Equals(NormalPath(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer progress must use this update's owned cache path.");
        UpdateCacheLocation.EnsureNormalPath(expected, false);
        return expected;
    }

    internal void Report(string stage, string detail, string status = "Active", bool migrationRequested = false,
        bool migrationCompleted = false, bool requiresRestart = false, bool recoveryWindowShown = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InstallerProgressJournal));
        if (_last != null && _last.Status != "Active") throw new InvalidOperationException("A finished installer result cannot be changed or retried through its progress journal.");
        if (_last?.MigrationCompleted == true && !migrationCompleted) throw new InvalidOperationException("Verified completed migration cannot become incomplete.");
        var record = new InstallerProgressRecord
        {
            SchemaVersion = 1, Id = _id, RequestHash = _requestHash, Sequence = (_last?.Sequence ?? 0) + 1,
            Stage = stage, Detail = detail, Status = status, UpdatedUtc = DateTime.UtcNow.ToString("O"),
            MigrationRequested = migrationRequested, MigrationCompleted = migrationCompleted, RequiresRestart = requiresRestart,
            RecoveryWindowShown = recoveryWindowShown || _last?.RecoveryWindowShown == true
        };
        ValidateRecord(record, _id, _requestHash);
        byte[] plain;
        using (var memory = new MemoryStream())
        { new DataContractJsonSerializer(typeof(InstallerProgressRecord)).WriteObject(memory, record); plain = memory.ToArray(); }
        byte[] encrypted;
        try
        {
            if (plain.Length > MaximumBytes) throw new IOException("The installer progress record exceeds its size limit.");
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        finally { Array.Clear(plain, 0, plain.Length); }
        if (encrypted.Length > MaximumBytes) throw new IOException("The protected installer progress record exceeds its size limit.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
            UpdateCacheLocation.EnsureNormalPath(_path, false);
            if (_last == null) File.Move(temporary, _path); // An unrelated record is never overwritten.
            else
            {
                var saved = ReadCore(_path, _id, _requestHash) ?? throw new IOException("The previous installer progress record disappeared.");
                if (saved.Sequence != _last.Sequence || saved.Status != "Active") throw new IOException("The installer progress record changed outside its writer.");
                File.Replace(temporary, _path, null);
            }
            _last = record;
        }
        finally
        {
            UpdateCacheLocation.EnsureNormalPath(temporary, false);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static InstallerProgressRecord? Read(string path, string expectedId, string expectedRequestHash)
    {
        ValidateIdentity(expectedId, expectedRequestHash);
        var normalized = NormalPath(path);
        // A read never creates a directory, journal or lease.
        if (!Directory.Exists(Path.GetDirectoryName(normalized)))
        { UpdateCacheLocation.EnsureNormalPath(normalized, false); return null; }
        var guards = GuardParents(normalized);
        try { return ReadCore(normalized, expectedId, expectedRequestHash); }
        finally { DisposeGuards(guards); }
    }

    internal static void PublishObserverReady(string path, string id, string requestHash)
    {
        ValidateIdentity(id, requestHash);
        var ready = NormalPath(path) + ".observer-ready";
        var guards = GuardParents(ready);
        try
        {
            if (IsObserverReady(path, id, requestHash)) return;
            var temporary = ready + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(id + "\n" + requestHash);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                UpdateCacheLocation.EnsureNormalPath(ready, false);
                File.Move(temporary, ready);
            }
            finally
            {
                UpdateCacheLocation.EnsureNormalPath(temporary, false);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { DisposeGuards(guards); }
    }

    internal static bool IsObserverReady(string path, string id, string requestHash)
    {
        ValidateIdentity(id, requestHash);
        var ready = NormalPath(path) + ".observer-ready";
        UpdateCacheLocation.EnsureNormalPath(ready, false);
        try
        {
            using var input = new FileStream(ready, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != 97) throw new IOException("The installation progress window acknowledgement is invalid.");
            using var reader = new StreamReader(input, Encoding.UTF8, false);
            if (reader.ReadToEnd() != id + "\n" + requestHash)
                throw new IOException("The installation progress window did not verify this update.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    private static InstallerProgressRecord? ReadCore(string path, string id, string requestHash)
    {
        // ReplaceFile briefly opens the destination with write access on Windows.
        // Keep denying in-place writers, and retry only that short sharing race.
        // Every attempt still checks the path, DPAPI authentication and identity.
        for (var attempt = 0; ; attempt++)
        {
            try { return ReadSnapshot(path, id, requestHash); }
            catch (IOException error) when (attempt < 10 && ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33))
            { System.Threading.Thread.Sleep(20); }
        }
    }

    private static InstallerProgressRecord? ReadSnapshot(string path, string id, string requestHash)
    {
        UpdateCacheLocation.EnsureNormalPath(path, false);
        byte[] encrypted;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length < 1 || input.Length > MaximumBytes) throw new InvalidDataException("Installer progress is empty or exceeds its size limit.");
            encrypted = new byte[(int)input.Length];
            for (var offset = 0; offset < encrypted.Length;)
            {
                var count = input.Read(encrypted, offset, encrypted.Length - offset);
                if (count == 0) throw new EndOfStreamException("Installer progress was truncated.");
                offset += count;
            }
        }
        catch (FileNotFoundException) { return null; }
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length > MaximumBytes) throw new InvalidDataException("Installer progress exceeds its metadata limit.");
            using var input = new MemoryStream(plain, writable: false);
            var record = (InstallerProgressRecord?)new DataContractJsonSerializer(typeof(InstallerProgressRecord)).ReadObject(input)
                ?? throw new InvalidDataException("Installer progress is empty.");
            ValidateRecord(record, id, requestHash);
            return record;
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    private static void ValidateRecord(InstallerProgressRecord record, string id, string hash)
    {
        if (record.SchemaVersion != 1 || record.Id != id || record.RequestHash != hash || record.Sequence < 1 ||
            !Stages.Contains(record.Stage) || record.Detail == null || record.Detail.Length > 2048 ||
            !DateTimeOffset.TryParseExact(record.UpdatedUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.Zero ||
            record.MigrationCompleted && (!record.MigrationRequested || record.RequiresRestart) ||
            record.RecoveryWindowShown && !record.MigrationRequested ||
            record.RequiresRestart && record.Status != "RestartRequired" ||
            !(record.Status == "Active" && record.Stage != "Complete" && record.Stage != "RestartRequired" && record.Stage != "Failed" ||
              record.Status == "Succeeded" && record.Stage == "Complete" ||
              record.Status == "RestartRequired" && record.Stage == "RestartRequired" && record.RequiresRestart ||
              record.Status == "Failed" && record.Stage == "Failed"))
            throw new InvalidDataException("Installer progress does not match the verified request or has invalid state.");
    }

    private static void ValidateIdentity(string id, string hash)
    {
        if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$") || !Regex.IsMatch(hash ?? "", "^[a-f0-9]{64}$"))
            throw new InvalidDataException("The installer progress identity is invalid.");
    }

    private static string NormalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' ||
            path[2] != '\\' || path.IndexOf(':', 2) >= 0 || path.IndexOf('"') >= 0 || path.IndexOf('\0') >= 0 ||
            path.Substring(3).Split('\\').Any(p => p == "." || p == ".." || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal)))
            throw new IOException("An unambiguous absolute local installer progress path is required.");
        return Path.GetFullPath(path).TrimEnd('\\');
    }

    private static List<SafeFileHandle> GuardParents(string path)
    {
        var parents = new Stack<string>();
        for (var current = Path.GetDirectoryName(path); current != null; current = Path.GetDirectoryName(current)) parents.Push(current);
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var parent in parents)
            {
                var handle = CreateFileW(@"\\?\" + parent, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The installer progress directory could not be locked safely.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); }
                handles.Add(handle);
                UpdateCacheLocation.EnsureNormalPath(parent, true);
            }
            return handles;
        }
        catch { DisposeGuards(handles); throw; }
    }

    private static void DisposeGuards(List<SafeFileHandle> handles)
    { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    public void Dispose()
    { if (_disposed) return; _disposed = true; _lease.Dispose(); DisposeGuards(_guards); }
}
