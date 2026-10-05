using System.IO.Pipes;
using System.Text;
using CloudBay.Core.Notifications;

namespace CloudBay.Core;

public enum ActivationDelivery { Delivered, Unavailable, AccessDenied }

/// <summary>Sends bounded commands to the existing per-user instance without creating a second client.</summary>
public static class ClientActivation
{
    public static async Task<ActivationDelivery> SendAsync(string pipeName, string command,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!IsSupportedCommand(command)) throw new ArgumentException("Unsupported instance command.", nameof(command));
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

    public static bool IsSupportedCommand(string? command) => command is "show" or "quit" or "tray" ||
        NotificationCommandCodec.TryDecode(command, out _);

    /// <summary>Byte-mode pipes can split a write. Read to EOF with a strict limit instead of acting on a prefix.</summary>
    public static async Task<string?> ReadCommandAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = new byte[NotificationCommandCodec.MaximumLength + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                var command = Encoding.UTF8.GetString(bytes, 0, length);
                return IsSupportedCommand(command) ? command : null;
            }
            length += read;
        }
        return null;
    }

    public static int ExitCode(ActivationDelivery delivery) => delivery switch
    {
        ActivationDelivery.Delivered => 0,
        ActivationDelivery.AccessDenied => 5,
        _ => 2
    };
}
