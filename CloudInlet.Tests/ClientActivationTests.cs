using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CloudInlet.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientActivationTests
{
    private static string PipeName() => "CloudInlet.Activation.Tests." + Guid.NewGuid().ToString("N");

    [DataTestMethod]
    [DataRow("launch")]
    [DataRow("show")]
    [DataRow("quit")]
    [DataRow("tray")]
    [Timeout(10_000)]
    public async Task ExistingInstanceReceivesTheExactCommand(string command)
    {
        var name = PipeName();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(timeout.Token);
        var send = ClientActivation.SendAsync(name, command, timeout.Token);
        await accept;
        using var reader = new StreamReader(server, Encoding.UTF8);
        Assert.AreEqual(command, await reader.ReadToEndAsync(timeout.Token));
        Assert.AreEqual(ActivationDelivery.Delivered, await send);
        Assert.AreEqual(0, ClientActivation.ExitCode(await send));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ProtectedInstanceAccessIsReportedWithoutAnUnhandledFailure()
    {
        var name = PipeName();
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.WriteData, AccessControlType.Deny));
        await using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var result = await ClientActivation.SendAsync(name, "quit");
        Assert.AreEqual(ActivationDelivery.AccessDenied, result);
        Assert.AreEqual(5, ClientActivation.ExitCode(result));
        Assert.IsFalse(server.IsConnected, "No command reaches the protected running instance.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task MissingInstanceHasABoundedUnavailableResult()
    {
        var result = await ClientActivation.SendAsync(PipeName(), "quit", timeout: TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(ActivationDelivery.Unavailable, result);
        Assert.AreEqual(2, ClientActivation.ExitCode(result));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CallerCancellationRemainsDistinctFromAnUnavailableInstance()
    {
        using var cancel = new CancellationTokenSource();
        var pending = ClientActivation.SendAsync(PipeName(), "show", cancel.Token);
        cancel.Cancel();
        try { await pending; Assert.Fail("Caller cancellation must propagate instead of returning an unavailable result."); }
        catch (OperationCanceledException) { Assert.IsTrue(cancel.IsCancellationRequested); }
    }

    [TestMethod]
    public async Task UnsupportedCommandsCannotBeSentToAnExistingInstance()
    {
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => ClientActivation.SendAsync(PipeName(), "delete"));
    }
}
