using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CloudInlet.Views;

/// <summary>A startup host that never creates a transfer controller while recovery is unresolved.</summary>
internal sealed class MigrationWindow : Window
{
    private readonly Grid _root;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _allowClose;
    internal MigrationWindow(string theme = "System")
    {
        Title = "CloudInlet";
        SystemBackdrop = new MicaBackdrop();
        _root = new Grid { Padding = new Thickness(24), RequestedTheme = theme switch
        { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default } };
        _root.Children.Add(new TextBlock { Text = "CloudInlet", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) });
        Content = _root;
        _root.Loaded += (_, _) => _ready.TrySetResult();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(960, 760));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", CloudInlet.Core.BuildInfo.IconFileName);
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += (_, args) => { if (!_allowClose) args.Cancel = true; };
    }

    internal async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        Activate();
        await _ready.Task;
        dialog.XamlRoot = _root.XamlRoot;
        dialog.RequestedTheme = _root.RequestedTheme;
        return await dialog.ShowAsync();
    }

    internal Task<ContentDialogResult> ShowErrorAsync(Exception error)
    {
        var details = "CloudInlet " + CloudInlet.Core.BuildInfo.Version + "\n" + error.Message;
        var dialog = new ContentDialog
        {
            Title = "CloudInlet couldn't start",
            Content = new TextBlock { Text = error.Message +
                "\n\nKeep your saved-data folders intact. Send these details to tahsinfaiyaz30@gmail.com for help recovering your profile.",
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "Copy details", CloseButtonText = "Close CloudInlet"
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(details);
                global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            }
            catch (Exception) { /* The selectable message remains available if the clipboard is busy. */ }
        };
        return ShowAsync(dialog);
    }

    internal void Finish() { _allowClose = true; Close(); }
}
