using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private readonly List<string> _navigationSearchHistory = [];

    private sealed record NavigationSearchSuggestion(string Title, string Glyph, string Hint = "",
        string? Route = null, bool IsRecent = false, bool IsClear = false)
    {
        public override string ToString() => Title;
    }

    private void InitializeNavigationSearch()
    {
        _navigationSearchHistory.AddRange(_controller.LoadSearchHistory());
        // The inner TextBox handles pointer input. Also catch a second click
        // after Escape has dismissed suggestions without moving focus away.
        NavigationSearch.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(NavigationSearch_PointerPressed), handledEventsToo: true);
    }

    private void NavigationSearch_GotFocus(object sender, RoutedEventArgs args)
    {
        if (!NavigationSearch.IsSuggestionListOpen && IsNavigationSearchInput(args.OriginalSource as DependencyObject))
            RefreshNavigationSearchSuggestions();
    }

    private void NavigationSearch_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (IsNavigationSearchInput(args.OriginalSource as DependencyObject))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && RootGrid.XamlRoot is { } root &&
                    IsNavigationSearchInput(FocusManager.GetFocusedElement(root) as DependencyObject))
                    RefreshNavigationSearchSuggestions();
            });
    }

    private bool IsNavigationSearchInput(DependencyObject? element)
    {
        var isTextBox = false;
        while (element is not null)
        {
            if (element is TextBox) isTextBox = true;
            if (ReferenceEquals(element, NavigationSearch)) return isTextBox;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void NavigationSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            RefreshNavigationSearchSuggestions();
    }

    private void RefreshNavigationSearchSuggestions()
    {
        var query = NavigationSearch.Text.Trim();
        var suggestions = new List<NavigationSearchSuggestion>();
        var recent = _navigationSearchHistory.Where(item => item.Contains(query, StringComparison.OrdinalIgnoreCase));
        foreach (var item in query.Length == 0 ? recent : recent.Take(3))
            suggestions.Add(new(item, "\uE81C", "Recent", IsRecent: true));
        if (query.Length == 0 && _navigationSearchHistory.Count > 0)
            suggestions.Add(new("Clear search history", "\uE74D", IsClear: true));
        foreach (var destination in FindDestinations(query).Take(8))
            suggestions.Add(new(destination.Title, NavigationSearchGlyph(destination.Route), Route: destination.Route));
        if (suggestions.Count == 0)
            suggestions.Add(new("No matching pages", "\uE721", "Try another search"));
        NavigationSearch.ItemsSource = suggestions;
        NavigationSearch.IsSuggestionListOpen = true;
    }

    private void NavigationSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var selected = args.ChosenSuggestion as NavigationSearchSuggestion;
        if (selected is { IsClear: true })
        {
            _navigationSearchHistory.Clear();
            _controller.SaveSearchHistory(_navigationSearchHistory);
            ReopenNavigationSearch();
            return;
        }
        if (selected is { IsRecent: true })
        {
            // Reuse the original query, so a broad search still offers all
            // matching pages rather than jumping to a previously chosen page.
            sender.Text = selected.Title;
            ReopenNavigationSearch();
            return;
        }
        var query = args.QueryText.Trim();
        if (query.Length is > 0 and <= 256)
        {
            _navigationSearchHistory.RemoveAll(item => item.Equals(query, StringComparison.OrdinalIgnoreCase));
            _navigationSearchHistory.Insert(0, query);
            if (_navigationSearchHistory.Count > 10) _navigationSearchHistory.RemoveRange(10, _navigationSearchHistory.Count - 10);
            _controller.SaveSearchHistory(_navigationSearchHistory);
        }
        var route = selected?.Route ?? (selected is null && query.Length > 0 ? FindDestinations(query).FirstOrDefault()?.Route : null);
        if (route is null)
        {
            ReopenNavigationSearch();
            return;
        }
        RequestNavigationRoute(route);
        sender.Text = "";
        sender.IsSuggestionListOpen = false;
        sender.ItemsSource = null;
        if (CanNavigateToParent) PageBreadcrumb.Focus(FocusState.Programmatic);
        else PageTitle.Focus(FocusState.Programmatic);
    }

    private void ReopenNavigationSearch() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed || !NavigationSearch.IsLoaded) return;
        NavigationSearch.Focus(FocusState.Programmatic);
        RefreshNavigationSearchSuggestions();
    });

    private static string NavigationSearchGlyph(string route) => route switch
    {
        "overview" => "\uE80F", "activity" => "\uE81C", "backup" => "\uE8B7", "files" => "\uE8A5",
        "services" or "settings/account" or "settings/onedrive" => "\uE753",
        _ => "\uE713"
    };
}
