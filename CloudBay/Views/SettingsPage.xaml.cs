using System.ComponentModel;
using CloudBay.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudBay.Views;

public sealed partial class SettingsPage : Page
{
    private ShellViewModel? _viewModel;
    private bool _syncingTheme;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel viewModel) return;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        SyncTheme();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTheme)) SyncTheme();
    }

    private void SyncTheme()
    {
        if (_viewModel is null) return;
        _syncingTheme = true;
        ThemeSelector.SelectedIndex = _viewModel.SelectedTheme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        _syncingTheme = false;
    }

    private async void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTheme || DataContext is not ShellViewModel viewModel) return;
        if (ThemeSelector.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            await viewModel.SetThemeAsync(theme);
        }
    }
}
