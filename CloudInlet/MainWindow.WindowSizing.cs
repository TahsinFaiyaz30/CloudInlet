using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace CloudInlet;

public sealed partial class MainWindow
{
    // Logical pixels: enough room for the compact navigation and a useful
    // scrolling page. Smaller/high-DPI displays cap these to their work area.
    private const double MinimumWindowWidth = 760;
    private const double MinimumWindowHeight = 600;
    private DisplayAreaWatcher? _windowDisplayWatcher;
    private XamlRoot? _windowSizingXamlRoot;
    private bool _windowSizeUpdateQueued;
    private bool _updatingWindowSizeConstraints;

    private void InitializeWindowSizeConstraints()
    {
        AppWindow.Changed += WindowSizeConstraints_Changed;
        RootGrid.Loaded += WindowSizeConstraints_Loaded;
        _windowDisplayWatcher = DisplayArea.CreateWatcher();
        _windowDisplayWatcher.Added += WindowDisplays_Changed;
        _windowDisplayWatcher.Removed += WindowDisplays_Changed;
        _windowDisplayWatcher.Updated += WindowDisplays_Changed;
        _windowDisplayWatcher.Start();
        UpdateWindowSizeConstraints();
    }

    private void WindowSizeConstraints_Loaded(object sender, RoutedEventArgs args)
    {
        if (_windowSizingXamlRoot is null && RootGrid.XamlRoot is { } root)
        {
            _windowSizingXamlRoot = root;
            root.Changed += WindowSizingXamlRoot_Changed;
        }
        QueueWindowSizeConstraintsUpdate();
    }

    private void WindowSizeConstraints_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
            QueueWindowSizeConstraintsUpdate();
    }

    private void WindowSizingXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => QueueWindowSizeConstraintsUpdate();
    private void WindowDisplays_Changed(DisplayAreaWatcher sender, DisplayArea args) => QueueWindowSizeConstraintsUpdate();

    private void QueueWindowSizeConstraintsUpdate()
    {
        // Display watcher callbacks need not arrive on the UI thread. Keep
        // the coalescing flags and all window access on the dispatcher.
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueWindowSizeConstraintsUpdate);
            return;
        }
        if (_closed || _windowSizeUpdateQueued || _updatingWindowSizeConstraints) return;
        _windowSizeUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _windowSizeUpdateQueued = false;
            if (!_closed) UpdateWindowSizeConstraints();
        })) _windowSizeUpdateQueued = false;
    }

    private void UpdateWindowSizeConstraints()
    {
        if (_closed || _updatingWindowSizeConstraints || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null || display.WorkArea.Width <= 0 || display.WorkArea.Height <= 0) return;

        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var workArea = display.WorkArea;
        var margin = (int)Math.Round(32 * scale);
        var minimumWidth = Math.Min((int)Math.Ceiling(MinimumWindowWidth * scale), Math.Max(1, workArea.Width - margin));
        var minimumHeight = Math.Min((int)Math.Ceiling(MinimumWindowHeight * scale), Math.Max(1, workArea.Height - margin));
        if (presenter.PreferredMinimumWidth == minimumWidth && presenter.PreferredMinimumHeight == minimumHeight) return;

        _updatingWindowSizeConstraints = true;
        try
        {
            // Native tracking limits enforce the boundary during resizing and
            // snapping, instead of repeatedly resizing an already-small window.
            presenter.PreferredMinimumWidth = minimumWidth;
            presenter.PreferredMinimumHeight = minimumHeight;
            if (presenter.State == OverlappedPresenterState.Restored &&
                (AppWindow.Size.Width < minimumWidth || AppWindow.Size.Height < minimumHeight))
                AppWindow.Resize(new SizeInt32(Math.Max(AppWindow.Size.Width, minimumWidth), Math.Max(AppWindow.Size.Height, minimumHeight)));
        }
        finally { _updatingWindowSizeConstraints = false; }
    }

    private void DisposeWindowSizeConstraints()
    {
        AppWindow.Changed -= WindowSizeConstraints_Changed;
        RootGrid.Loaded -= WindowSizeConstraints_Loaded;
        if (_windowSizingXamlRoot is { } root) root.Changed -= WindowSizingXamlRoot_Changed;
        _windowSizingXamlRoot = null;
        if (_windowDisplayWatcher is not { } watcher) return;
        watcher.Added -= WindowDisplays_Changed;
        watcher.Removed -= WindowDisplays_Changed;
        watcher.Updated -= WindowDisplays_Changed;
        watcher.Stop();
        _windowDisplayWatcher = null;
    }
}
