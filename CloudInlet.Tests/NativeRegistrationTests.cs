using System.Runtime.InteropServices;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Provider;

namespace CloudInlet.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeRegistrationTests
{
    [TestMethod]
    public async Task FailedHiddenFolderRegistrationRollsBackWithoutDeletingExistingLocalData()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-RegistrationFailure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "existing.txt");
        await File.WriteAllTextAsync(local, "Existing local data survives setup failure.");
        await using var service = new WindowsPlaceholderService();
        try
        {
            await Assert.ThrowsExceptionAsync<IOException>(() => service.ConnectAsync(root, "failure-" + Guid.NewGuid().ToString("N"), (_, _, _, _, _) => Task.CompletedTask));
            Assert.IsNull(service.RegistrationId, "A failed new setup must roll back its Shell registration.");
            Assert.AreEqual("Existing local data survives setup failure.", await File.ReadAllTextAsync(local));
            Assert.IsTrue(CloudFilesNative.CfGetSyncRootInfoByPath(root, 0, out _, 8, out _) < 0,
                "The failed setup must leave no native sync-root registration.");
            Assert.IsFalse((File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0);
        }
        finally { DeleteGeneratedRoot(root); }
    }

    [TestMethod]
    public async Task FailedSecondConnectionNeverUnregistersExistingAccountData()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudInlet-ExistingRegistration-" + Guid.NewGuid().ToString("N"));
        var account = "existing-" + Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes("Existing provider still hydrates its files.");
        await using var existing = new WindowsPlaceholderService();
        await using var rejected = new WindowsPlaceholderService();
        try
        {
            await existing.ConnectAsync(root, account, (_, offset, length, destination, token) =>
                destination.WriteAsync(bytes.AsMemory((int)offset, (int)length), token).AsTask());
            AssertStoredNativePolicies(root);
            var path = Path.Combine(root, "existing.bin");
            await existing.CreateOrUpdateAsync(path, new("existing", "existing.bin", bytes.Length, null, DateTimeOffset.UtcNow), true);
            try { await rejected.ConnectAsync(root, account, (_, _, _, _, _) => Task.CompletedTask); Assert.Fail("Windows must reject a second active provider connection."); }
            catch (COMException) { }
            catch (IOException) { }
            Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(r => r.Id == existing.RegistrationId));
            Assert.IsTrue(existing.IsPlaceholder(path));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await rejected.DisconnectAsync();
            await existing.DisconnectAsync();
            if (existing.RegistrationId is { } registrationId) StorageProviderSyncRootManager.Unregister(registrationId);
            DeleteGeneratedRoot(root);
        }
    }

    [TestMethod]
    public async Task ExplicitHydrationPreservesUserPinState()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudInlet-PinHydration-" + Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("hydrate without changing the pin preference");
        await using var service = new WindowsPlaceholderService();
        try
        {
            await service.ConnectAsync(root, "pin-" + Guid.NewGuid().ToString("N"), (_, offset, length, destination, token) =>
                destination.WriteAsync(bytes.AsMemory((int)offset, (int)length), token).AsTask());
            var path = Path.Combine(root, "pin.bin");
            await service.CreateOrUpdateAsync(path, new("pin", "pin.bin", bytes.Length, null, DateTimeOffset.UtcNow), true);
            await service.SetPinAsync(path, PinMode.OnlineOnly);
            var onlinePreference = GetPin(path);
            await service.HydrateAsync(path);
            Assert.AreEqual(onlinePreference, GetPin(path), "Hydration must preserve the online-only preference.");
            await service.FreeSpaceAsync(path);
            using (var handle = CloudFilesNative.Open(path, writable: true))
                CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle, 1, 0, IntPtr.Zero));
            await service.HydrateAsync(path);
            Assert.AreEqual(1u, GetPin(path), "Hydration must preserve an always-available pin.");
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            await service.DisconnectAsync();
            if (service.RegistrationId is { } registration) StorageProviderSyncRootManager.Unregister(registration);
            DeleteGeneratedRoot(root);
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task FailedUnregisterPreparationRetainsRegistrationAndCanRetryHydration()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudInlet-UnregisterFailure-" + Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("cloud content must survive a failed disconnect");
        var offline = true;
        await using var service = new WindowsPlaceholderService();
        try
        {
            await service.ConnectAsync(root, "unregister-" + Guid.NewGuid().ToString("N"), (_, offset, length, destination, token) =>
                offline ? Task.FromException(new HttpRequestException("Simulated offline hydration.")) :
                destination.WriteAsync(bytes.AsMemory((int)offset, (int)length), token).AsTask());
            var cloudPath = Path.Combine(root, "nested", "online.bin");
            var localPath = Path.Combine(root, "local.txt");
            await service.CreateOrUpdateAsync(cloudPath, new("offline", "online.bin", bytes.Length, null, DateTimeOffset.UtcNow), true);
            await File.WriteAllTextAsync(localPath, "unuploaded local contents");
            Exception? failure = null;
            try { await service.PrepareForUnregisterAsync(); }
            catch (Exception error) { failure = error; }
            Assert.IsTrue(failure is IOException or COMException, "Failed hydration must abort preparation.");
            StringAssert.Contains(failure!.Message, root);
            StringAssert.Contains(failure.Message, "0x");
            StringAssert.Contains(failure.Message, "retry");
            StringAssert.Contains(failure.Message, "registration");
            Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(r => r.Id == service.RegistrationId));
            Assert.IsTrue(service.IsPlaceholder(cloudPath));
            Assert.AreEqual("unuploaded local contents", await File.ReadAllTextAsync(localPath));

            offline = false;
            await service.PrepareForUnregisterAsync();
            Assert.IsFalse(service.IsPlaceholder(cloudPath));
            await service.DisconnectAsync();
            StorageProviderSyncRootManager.Unregister(service.RegistrationId!);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(cloudPath));
            Assert.AreEqual("unuploaded local contents", await File.ReadAllTextAsync(localPath));
        }
        finally
        {
            await service.DisconnectAsync();
            if (service.RegistrationId is { } registration && StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(r => r.Id == registration))
                StorageProviderSyncRootManager.Unregister(registration);
            DeleteGeneratedRoot(root);
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RestartedRootUnregisterPreservesNestedFilesAndEmptyFolders()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudInlet-RestartUnregister-" + Guid.NewGuid().ToString("N"));
        var account = "restart-" + Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes("nested cloud file survives a provider restart");
        var onlinePath = Path.Combine(root, "nested", "online.bin");
        var emptyPath = Path.Combine(root, "empty");
        var internalPath = Path.Combine(root, ".cloudbay", "Recovery", "removed.bin");
        await using var first = new WindowsPlaceholderService();
        await using var second = new WindowsPlaceholderService();
        HydrationHandler hydrate = (_, offset, length, destination, token) =>
            destination.WriteAsync(bytes.AsMemory((int)offset, (int)length), token).AsTask();
        try
        {
            await first.ConnectAsync(root, account, hydrate);
            Directory.CreateDirectory(emptyPath);
            await first.CreateOrUpdateAsync(onlinePath, new("nested", "online.bin", bytes.Length, null, DateTimeOffset.UtcNow), true);
            await first.CreateOrUpdateAsync(internalPath, new("recovery", "removed.bin", bytes.Length, null, DateTimeOffset.UtcNow), true);
            await first.DisconnectAsync();
            await second.ConnectAsync(root, account, hydrate);
            AssertStoredNativePolicies(root);
            await second.PrepareForUnregisterAsync();
            await second.DisconnectAsync();
            StorageProviderSyncRootManager.Unregister(second.RegistrationId!);
            Assert.IsTrue(Directory.Exists(emptyPath));
            Assert.IsFalse(second.IsPlaceholder(onlinePath));
            Assert.IsFalse(second.IsPlaceholder(internalPath));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(onlinePath));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(internalPath));
        }
        finally
        {
            await second.DisconnectAsync();
            await first.DisconnectAsync();
            if (first.RegistrationId is { } registration && StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(r => r.Id == registration))
                StorageProviderSyncRootManager.Unregister(registration);
            DeleteGeneratedRoot(root);
        }
    }

    private static uint GetPin(string path)
    {
        using var handle = CloudFilesNative.Open(path);
        var buffer = Marshal.AllocHGlobal(4160);
        try
        {
            CloudFilesNative.Check(CloudFilesNative.CfGetPlaceholderInfo(handle, 1, buffer, 4160, out _));
            return (uint)Marshal.ReadInt32(buffer, 32);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void DeleteGeneratedRoot(string root)
    {
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(resolved);
        var userProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var name = Path.GetFileName(resolved);
        var prefixes = new[] { "CloudInlet-RegistrationFailure-", "CloudInlet-ExistingRegistration-", "CloudInlet-PinHydration-",
            "CloudInlet-UnregisterFailure-", "CloudInlet-RestartUnregister-" };
        if (!(string.Equals(parent, userProfile, StringComparison.OrdinalIgnoreCase) || string.Equals(parent, temporary, StringComparison.OrdinalIgnoreCase)) ||
            !prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _)))
            throw new IOException("Refusing cleanup outside a uniquely generated native registration test root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }

    private static void AssertStoredNativePolicies(string root)
    {
        // WinRT GetSyncRootInformationForId returns default values for several policies on Windows.
        // Query CF_SYNC_ROOT_STANDARD_INFO directly; these offsets match the installed x64 SDK ABI.
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            CloudFilesNative.Check(GetSyncRootStandardInfo(root, 1, buffer, 8192, out var returned));
            Assert.IsTrue(returned >= 1056);
            Assert.AreEqual((short)2, Marshal.ReadInt16(buffer, 8), "Windows must store FULL hydration.");
            Assert.AreEqual(4, Marshal.ReadInt16(buffer, 10) & 4, "Windows must permit automatic Storage Sense dehydration.");
            Assert.AreEqual((short)3, Marshal.ReadInt16(buffer, 12), "Windows must store ALWAYS_FULL population.");
            Assert.AreEqual(0x100, Marshal.ReadInt32(buffer, 16) & 0x100, "Windows must track file last-write changes.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, EntryPoint = "CfGetSyncRootInfoByPath")]
    private static extern int GetSyncRootStandardInfo(string path, uint infoClass, IntPtr buffer, uint capacity, out uint returned);
}
