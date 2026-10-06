using System.Text.Json;
using System.Xml.Linq;
using CloudInlet.Core.Notifications;
using Microsoft.Windows.AppNotifications;

namespace CloudInlet.Windows;

/// <summary>Exercises native notification storage from a unique copied executable, without client/account initialization.</summary>
internal static class NativeNotificationValidation
{
    public static async Task<bool> RunAsync(string output)
    {
        var executableRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var runRoot = Path.GetDirectoryName(executableRoot)!;
        var evidenceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        if (!Path.GetFileName(runRoot).StartsWith("notification-native-", StringComparison.Ordinal) ||
            Path.GetFileName(executableRoot) != "App" || Path.GetDirectoryName(evidenceRoot) != runRoot)
            throw new InvalidOperationException("Native notification validation requires an isolated notification-native-* copied App and sibling evidence directory.");
        Directory.CreateDirectory(evidenceRoot);
        var started = DateTimeOffset.UtcNow;
        var manager = AppNotificationManager.Default;
        var registered = false;
        var passed = false;
        var cleaned = false;
        string stage = "Register isolated executable";
        try
        {
            manager.NotificationInvoked += IgnoreActivation;
            manager.Register("CloudInlet Notification Validation", new Uri(Path.Combine(executableRoot, "Assets", CloudInlet.Core.BuildInfo.NotificationIconFileName)));
            registered = true;
            stage = "Build allowlisted native payload";
            var command = new NotificationCommand(NotificationAction.InstallUpdate, "1.0.0");
            var notice = new NotificationNotice("validation", "native", "CloudInlet notification validation",
                "This isolated notification checks native storage without displaying a popup.", new(NotificationAction.ViewUpdates),
                [new("Install update", command), new("View update", new(NotificationAction.ViewUpdates))]);
            var xml = NotificationContent.CreateXml(notice, false);
            stage = "Store suppressed native notification";
            var notification = new AppNotification(xml) { Group = "validation", Tag = "native", SuppressDisplay = true,
                Expiration = DateTimeOffset.UtcNow.AddMinutes(1) };
            manager.Show(notification);
            if (notification.Id == 0) throw new InvalidOperationException("Windows did not accept the isolated notification.");
            stage = "Read native notification and exact action payloads";
            var stored = (await ReadAllAsync(manager).WaitAsync(TimeSpan.FromSeconds(5)))
                .Single(item => item.Id == notification.Id && item.Tag == "native" && item.Group == "validation");
            var document = XDocument.Parse(stored.Payload);
            var actions = document.Descendants("action").Select(item => item.Attribute("arguments")!.Value).ToArray();
            if (actions.Length != 2 || !NotificationCommandCodec.TryDecode(actions[0], out var install) || install != command ||
                !NotificationCommandCodec.TryDecode(actions[1], out var view) || view.Action != NotificationAction.ViewUpdates)
                throw new InvalidOperationException("Native button payload changed.");
            stage = "Remove isolated native group";
            await RemoveGroupAsync(manager).WaitAsync(TimeSpan.FromSeconds(5));
            if ((await ReadAllAsync(manager).WaitAsync(TimeSpan.FromSeconds(5))).Any(item => item.Group == "validation"))
                throw new InvalidOperationException("The isolated group was not removed.");
            passed = true;
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "native-checks.json"), JsonSerializer.Serialize(new
            {
                nativeRegistration = true, suppressedNativeNotificationAccepted = true, exactNativeButtonArguments = actions,
                groupRemoval = true, setting = manager.Setting.ToString(), realClientInitialized = false,
                popupDisplayed = false, startedUtc = started, completedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "failure.json"), JsonSerializer.Serialize(new
            { stage, exceptionType = error.GetType().FullName, hresult = $"0x{error.HResult:X8}", startedUtc = started }));
        }
        finally
        {
            if (registered)
            {
                try { await RemoveAllAsync(manager).WaitAsync(TimeSpan.FromSeconds(5)); manager.UnregisterAll(); cleaned = true; }
                catch (Exception) { passed = false; }
            }
            try { manager.NotificationInvoked -= IgnoreActivation; } catch (Exception) { }
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "complete.json"), JsonSerializer.Serialize(new
            { success = passed && cleaned, registrationRemoved = cleaned, completedUtc = DateTimeOffset.UtcNow }));
        }
        return passed && cleaned;

        static async Task<IList<AppNotification>> ReadAllAsync(AppNotificationManager manager) => await manager.GetAllAsync();
        static async Task RemoveGroupAsync(AppNotificationManager manager) => await manager.RemoveByGroupAsync("validation");
        static async Task RemoveAllAsync(AppNotificationManager manager) => await manager.RemoveAllAsync();
        static void IgnoreActivation(AppNotificationManager manager, AppNotificationActivatedEventArgs args) { }
    }
}
