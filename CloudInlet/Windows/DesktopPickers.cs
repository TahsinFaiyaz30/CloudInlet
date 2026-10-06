using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace CloudInlet.Windows;

/// <summary>
/// Windows App SDK desktop pickers return paths and also work when CloudBay is elevated.
/// Invoke these methods on the owning window's UI thread. These helpers return paths
/// without reading the selected file contents.
/// </summary>
public static class DesktopPickers
{
    private static readonly HashSet<nint> PendingOwners = [];

    public static Task<string?> PickFileAsync(nint owner, string commitButtonText = "Choose file",
        IEnumerable<string>? fileTypeFilters = null, CancellationToken cancellationToken = default) => RunAsync(owner, "file", async windowId =>
    {
        var picker = CreateFilePicker(windowId, commitButtonText, fileTypeFilters);
        var result = await picker.PickSingleFileAsync().AsTask(cancellationToken);
        return result?.Path;
    }, cancelled: (string?)null, cancellationToken);

    public static Task<IReadOnlyList<string>> PickFilesAsync(nint owner, string commitButtonText = "Choose files",
        IEnumerable<string>? fileTypeFilters = null, CancellationToken cancellationToken = default) => RunAsync<IReadOnlyList<string>>(owner, "file", async windowId =>
    {
        var picker = CreateFilePicker(windowId, commitButtonText, fileTypeFilters);
        var results = await picker.PickMultipleFilesAsync().AsTask(cancellationToken);
        return results.Select(result => result.Path).ToArray();
    }, cancelled: Array.Empty<string>(), cancellationToken);

    public static Task<string?> PickFolderAsync(nint owner, string commitButtonText = "Choose folder",
        CancellationToken cancellationToken = default) =>
        RunAsync(owner, "folder", async windowId =>
        {
            var picker = new FolderPicker(windowId)
            {
                CommitButtonText = commitButtonText,
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            var result = await picker.PickSingleFolderAsync().AsTask(cancellationToken);
            return result?.Path;
        }, cancelled: (string?)null, cancellationToken);

    private static FileOpenPicker CreateFilePicker(WindowId owner, string commitButtonText,
        IEnumerable<string>? fileTypeFilters)
    {
        var picker = new FileOpenPicker(owner)
        {
            CommitButtonText = commitButtonText,
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };
        var filters = fileTypeFilters?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        // Leaving FileTypeFilter empty is the documented App SDK all-files setting.
        // The old Windows.Storage.Pickers required adding "*" instead.
        if (!filters.Any(filter => filter is "*" or "*.*"))
            foreach (var filter in filters) picker.FileTypeFilter.Add(filter);
        return picker;
    }

    private static async Task<T> RunAsync<T>(nint owner, string kind, Func<WindowId, Task<T>> choose, T cancelled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner == 0 || !IsWindow(owner))
            throw new InvalidOperationException("The CloudInlet window is not ready to open a chooser. Reopen the window and try again.");
        var ownerThread = GetWindowThreadProcessId(owner, out var ownerProcess);
        if (ownerProcess != (uint)Environment.ProcessId || ownerThread != GetCurrentThreadId())
            throw new InvalidOperationException("The chooser must be opened from the CloudInlet window's UI thread.");
        lock (PendingOwners)
            if (!PendingOwners.Add(owner))
                throw new InvalidOperationException("Finish or cancel the open file or folder chooser first.");
        try
        {
            return await choose(Win32Interop.GetWindowIdFromWindow(owner));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return cancelled; }
        catch (COMException error) when (error.HResult == unchecked((int)0x800704C7))
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("The chooser was cancelled.", error, cancellationToken);
            return cancelled;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Some Windows COM failures have an empty Message. Preserve their diagnostic
            // HRESULT without leaving the user with an unexplained red error banner.
            throw new IOException($"Windows could not open the {kind} chooser (0x{error.HResult:X8}). Try again or reopen CloudInlet.", error);
        }
        finally
        {
            lock (PendingOwners) PendingOwners.Remove(owner);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
