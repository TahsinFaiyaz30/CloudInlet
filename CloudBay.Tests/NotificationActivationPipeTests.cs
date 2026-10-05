using System.IO.Pipes;
using CloudBay.Core;
using CloudBay.Core.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class NotificationActivationPipeTests
{
    [DataTestMethod]
    [DataRow(NotificationAction.ViewActivity)]
    [DataRow(NotificationAction.ManageBackup)]
    [DataRow(NotificationAction.OpenFolder)]
    [DataRow(NotificationAction.RetrySync)]
    [DataRow(NotificationAction.ViewUpdates)]
    [DataRow(NotificationAction.DownloadUpdate)]
    [DataRow(NotificationAction.InstallUpdate)]
    [Timeout(10_000)]
    public async Task NotificationActionReachesOnlyItsPerUserFlavorPipe(NotificationAction action)
    {
        var name = "CloudBay.NotificationPipe.Tests." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var target = new NamedPipeServerStream(name + ".Debug", PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var other = new NamedPipeServerStream(name + ".Release", PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = target.WaitForConnectionAsync(timeout.Token);
        var command = new NotificationCommand(action, action is NotificationAction.DownloadUpdate or NotificationAction.InstallUpdate ? "1.0.0" : null);
        var encoded = NotificationCommandCodec.Encode(command);
        var sent = ClientActivation.SendAsync(name + ".Debug", encoded, timeout.Token);
        await accept;
        Assert.AreEqual(encoded, await ClientActivation.ReadCommandAsync(target, timeout.Token));
        Assert.AreEqual(ActivationDelivery.Delivered, await sent);
        Assert.IsFalse(other.IsConnected, "The other flavor must never receive notification commands.");
    }
}
