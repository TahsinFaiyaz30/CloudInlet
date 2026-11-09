namespace CloudInlet.Windows;

/// <summary>Only read contention is recoverable. Authentication and identity errors remain failures.</summary>
internal static class InstallationRecoveryPolling
{
    internal static async Task<T?> ReadAsync<T>(Func<T> read) where T : class
    {
        try { return await Task.Run(read); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }
}
