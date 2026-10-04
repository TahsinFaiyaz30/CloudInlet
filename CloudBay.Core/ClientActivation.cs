using System.IO.Pipes;
using System.Text;

namespace CloudBay.Core;

public enum ActivationDelivery { Delivered, Unavailable, AccessDenied }

/// <summary>Sends bounded commands to the existing per-user instance without creating a second client.</summary>
public static class ClientActivation
{
    public static async Task<ActivationDelivery> SendAsync(string pipeName, string command,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (command is not ("show" or "quit" or "tray")) throw new ArgumentException("Unsupported instance command.", nameof(command));
        var deadline = timeout ?? TimeSpan.FromSeconds(3);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(deadline);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(bounded.Token).ConfigureAwait(false);
            await client.WriteAsync(Encoding.UTF8.GetBytes(command), bounded.Token).ConfigureAwait(false);
            await client.FlushAsync(bounded.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ActivationDelivery.Delivered;
        }
        catch (UnauthorizedAccessException) { return ActivationDelivery.AccessDenied; }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070005)) { return ActivationDelivery.AccessDenied; }
        catch (IOException) { return ActivationDelivery.Unavailable; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ActivationDelivery.Unavailable; }
    }

    public static int ExitCode(ActivationDelivery delivery) => delivery switch
    {
        ActivationDelivery.Delivered => 0,
        ActivationDelivery.AccessDenied => 5,
        _ => 2
    };
}
