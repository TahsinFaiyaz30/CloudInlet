using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Packaging;

/// <summary>A request-bound handoff decision. Parent exit alone never authorizes a progress-enabled update.</summary>
internal static class InstallerProgressHandoff
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudInlet.InstallerProgressHandoff.v1");
    [DataContract] private sealed class Decision
    {
        [DataMember(IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(IsRequired = true)] public string Id { get; set; } = "";
        [DataMember(IsRequired = true)] public string RequestHash { get; set; } = "";
        [DataMember(IsRequired = true)] public string State { get; set; } = "";
    }

    internal static void Create(string progressPath, string id, string requestHash) => Publish(progressPath, id, requestHash, "Pending");
    internal static void Commit(string progressPath, string id, string requestHash) => Publish(progressPath, id, requestHash, "Committed");
    internal static void Cancel(string progressPath, string id, string requestHash) => Publish(progressPath, id, requestHash, "Canceled");

    internal static string? ReadState(string progressPath, string id, string requestHash)
    {
        var path = PathFor(progressPath, id, requestHash);
        using var guards = new ParentGuards(path);
        return Read(path, id, requestHash)?.State;
    }

    private static string PathFor(string progressPath, string id, string hash)
    {
        if (hash == null || hash.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("The progress handoff request identity is invalid.");
        var cache = Path.GetDirectoryName(Path.GetDirectoryName(progressPath));
        if (cache == null) throw new IOException("The progress handoff path is invalid.");
        return InstallerProgressJournal.ValidatePath(cache, progressPath, id) + ".handoff.dpapi";
    }

    private static void Publish(string progressPath, string id, string requestHash, string state)
    {
        var path = PathFor(progressPath, id, requestHash);
        using var guards = new ParentGuards(path);
        UpdateCacheLocation.EnsureNormalPath(path + ".lock", false);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = Read(path, id, requestHash);
        if (state == "Pending" && previous != null) throw new IOException("This update already has a handoff decision.");
        if (state != "Pending" && previous == null) throw new IOException("The update handoff review is missing.");
        if (previous?.State == state) return;
        if (previous?.State == "Canceled") throw new IOException("A canceled update handoff cannot be committed or reused.");
        var record = new Decision { SchemaVersion = 1, Id = id, RequestHash = requestHash, State = state };
        byte[] plain;
        using (var memory = new MemoryStream())
        { new DataContractJsonSerializer(typeof(Decision)).WriteObject(memory, record); plain = memory.ToArray(); }
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(plain, 0, plain.Length); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
            UpdateCacheLocation.EnsureNormalPath(path, false);
            if (previous == null) File.Move(temporary, path);
            else File.Replace(temporary, path, null);
        }
        finally
        {
            UpdateCacheLocation.EnsureNormalPath(temporary, false);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Decision? Read(string path, string id, string hash)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return ReadSnapshot(path, id, hash); }
            catch (IOException error) when (attempt < 10 && ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33))
            { System.Threading.Thread.Sleep(20); }
        }
    }

    private static Decision? ReadSnapshot(string path, string id, string hash)
    {
        UpdateCacheLocation.EnsureNormalPath(path, false);
        byte[] encrypted;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length < 1 || input.Length > 4096) throw new InvalidDataException("The update handoff decision has an invalid size.");
            encrypted = new byte[(int)input.Length];
            for (var offset = 0; offset < encrypted.Length;)
            {
                var count = input.Read(encrypted, offset, encrypted.Length - offset);
                if (count == 0) throw new EndOfStreamException("The update handoff decision was truncated.");
                offset += count;
            }
        }
        catch (FileNotFoundException) { return null; }
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length > 4096) throw new InvalidDataException("The update handoff decision is too large.");
            using var input = new MemoryStream(plain, false);
            var value = (Decision?)new DataContractJsonSerializer(typeof(Decision)).ReadObject(input);
            if (value == null || value.SchemaVersion != 1 || value.Id != id || value.RequestHash != hash ||
                (value.State != "Pending" && value.State != "Committed" && value.State != "Canceled"))
                throw new InvalidDataException("The update handoff decision does not match this verified request.");
            return value;
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    private sealed class ParentGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _held = new();
        internal ParentGuards(string path)
        {
            var parents = new Stack<string>();
            for (var current = Path.GetDirectoryName(path); current != null; current = Path.GetDirectoryName(current)) parents.Push(current);
            try
            {
                foreach (var parent in parents)
                {
                    var handle = CreateFileW(@"\\?\" + parent, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The update handoff directory is busy or unavailable."); }
                    _held.Add(handle);
                    UpdateCacheLocation.EnsureNormalPath(parent, true);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = _held.Count - 1; i >= 0; i--) _held[i].Dispose(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
