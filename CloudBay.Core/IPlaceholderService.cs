namespace CloudBay.Core;

public readonly record struct PlaceholderFileState(bool IsPlaceholder, bool IsHydrated, bool HasLocalChanges);

public interface IPlaceholderService : IAsyncDisposable
{
    Task ConnectAsync(string rootPath, string accountIdentity, HydrationHandler hydrate, CancellationToken cancellationToken = default);
    Task CreateOrUpdateAsync(string fullPath, CloudObject file, bool inSync, CancellationToken cancellationToken = default);
    Task MarkInSyncAsync(string fullPath, CloudObject file, CancellationToken cancellationToken = default);
    bool IsPlaceholder(string fullPath);
    bool IsHydrated(string fullPath);
    // Native dirty ranges detect content edits even when an application restores size and timestamps.
    bool HasLocalChanges(string fullPath) => false;
    // Providers can inspect all three metadata facts through one no-recall handle.
    PlaceholderFileState GetFileState(string fullPath)
    {
        var placeholder = IsPlaceholder(fullPath);
        return new(placeholder, !placeholder || IsHydrated(fullPath), HasLocalChanges(fullPath));
    }
    Task SetPinAsync(string fullPath, PinMode mode, CancellationToken cancellationToken = default);
    // Providers can override this to fetch missing ranges without changing the user's pin preference.
    Task HydrateAsync(string fullPath, CancellationToken cancellationToken = default) =>
        SetPinAsync(fullPath, PinMode.Available, cancellationToken);
    Task FreeSpaceAsync(string fullPath, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}
