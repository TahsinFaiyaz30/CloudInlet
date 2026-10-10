using CloudInlet.Core.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

internal sealed record TransferExclusionRule(string Pattern, bool Enabled);

/// <summary>Edits only this transfer's legacy name/path patterns, without writing backup preferences.</summary>
internal sealed class TransferExclusionEditor : UserControl
{
    private enum EditorKind { Type, Name, Path, Folder, Advanced }
    private sealed class Rule(string pattern, bool enabled = true)
    {
        internal string Pattern { get; set; } = pattern;
        internal bool Enabled { get; set; } = enabled;
    }

    private readonly List<Rule> _rules;
    private readonly StackPanel _root = new() { Spacing = 16 };
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly Button _add = new() { MinHeight = 36, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _empty;
    private readonly Border _builder;
    private readonly StackPanel _fields = new() { Spacing = 16 };
    private readonly TextBlock _title = SourceImportDialog.Heading("");
    private readonly TextBlock _help = SourceImportDialog.Text("", true);
    private readonly TextBlock _preview = SourceImportDialog.Text("", true);
    private readonly TextBlock _exampleResult = SourceImportDialog.Text("", true);
    private readonly TextBox _value = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _match = new() { Header = "Match names that", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _example = new() { Header = "Path inside the source folder", PlaceholderText = "For example, Projects/cache/report.txt" };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false, Visibility = Visibility.Collapsed };
    private readonly Button _save = SourceImportDialog.ActionButton("Add rule");
    private readonly Button _cancel = SourceImportDialog.ActionButton("Cancel");
    private readonly global::Windows.UI.ViewManagement.UISettings _textSettings = new();
    private EditorKind _kind;
    private Rule? _editing;
    private bool _refreshing;

    /// <summary>The exact enabled patterns expected by TransferJobPlan.Exclusions.</summary>
    internal IReadOnlyList<string> Patterns => _rules.Where(rule => rule.Enabled).Select(rule => rule.Pattern).ToArray();
    /// <summary>A detached snapshot, including disabled rules, for resuming after account connection.</summary>
    internal IReadOnlyList<TransferExclusionRule> Rules => _rules.Select(rule => new TransferExclusionRule(rule.Pattern, rule.Enabled)).ToArray();
    internal bool IsEditing => _builder.Visibility == Visibility.Visible;
    internal event EventHandler? StateChanged;

    internal TransferExclusionEditor(IEnumerable<string> initialPatterns)
        : this((initialPatterns ?? throw new ArgumentNullException(nameof(initialPatterns)))
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern)).Select(pattern => new TransferExclusionRule(pattern.Trim(), true)))
    {
    }

    internal TransferExclusionEditor(IEnumerable<TransferExclusionRule> initialRules)
    {
        ArgumentNullException.ThrowIfNull(initialRules);
        // The previous transfer text box trimmed lines. Preserve those same inputs and
        // the legacy matcher; guided backup exclusions have different path semantics.
        _rules = initialRules.Select(rule => new Rule(rule.Pattern, rule.Enabled)).ToList();
        _add.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        var addContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addContent.Children.Add(CommandIcon("\uE710", 14));
        addContent.Children.Add(new TextBlock { Text = "Add exclusion" });
        _add.Content = addContent;
        AutomationProperties.SetName(_add, "Add transfer exclusion");
        var addMenu = new MenuFlyout();
        FluentIconMotion.Attach(addMenu);
        AddChoice(addMenu, "File type", "\uE8A5", EditorKind.Type);
        AddChoice(addMenu, "Name rule", "\uE71C", EditorKind.Name);
        AddChoice(addMenu, "File path", "\uE8A5", EditorKind.Path);
        AddChoice(addMenu, "Folder path", "\uE8B7", EditorKind.Folder);
        addMenu.Items.Add(new MenuFlyoutSeparator());
        AddChoice(addMenu, "Advanced pattern", "\uE713", EditorKind.Advanced);
        _add.Flyout = addMenu;

        var introduction = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var heading = SourceImportDialog.Heading("Excluded files and folders");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        introduction.Children.Add(heading);
        introduction.Children.Add(SourceImportDialog.Text("Choose what this transfer skips. Your backup exclusions stay unchanged.", true));
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header.Children.Add(introduction);
        header.Children.Add(_add);
        void ReflowHeader()
        {
            _add.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var stacked = header.ActualWidth < 340 * _textSettings.TextScaleFactor + _add.DesiredSize.Width + 16;
            Grid.SetColumnSpan(introduction, stacked ? 2 : 1);
            Grid.SetColumn(_add, stacked ? 0 : 1);
            Grid.SetRow(_add, stacked ? 1 : 0);
            Grid.SetColumnSpan(_add, stacked ? 2 : 1);
            _add.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            header.RowSpacing = stacked ? 12 : 0;
        }
        header.SizeChanged += (_, _) => ReflowHeader();
        header.Loaded += (_, _) => ReflowHeader();
        _root.Children.Add(header);

        var emptyContent = new Grid { ColumnSpacing = 16 };
        emptyContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        emptyContent.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var emptyIcon = FluentIcons.Create("folder", 28);
        emptyIcon.VerticalAlignment = VerticalAlignment.Center;
        emptyContent.Children.Add(emptyIcon);
        var emptyCopy = new StackPanel { Spacing = 4 };
        emptyCopy.Children.Add(StrongText("Everything is included"));
        emptyCopy.Children.Add(SourceImportDialog.Text("Add a file type, name, or relative path to leave it out of this transfer.", true));
        Grid.SetColumn(emptyCopy, 1);
        emptyContent.Children.Add(emptyCopy);
        _empty = SourceImportDialog.Surface(emptyContent);
        _root.Children.Add(_empty);
        _root.Children.Add(_rows);

        var builderBody = new StackPanel { Spacing = 16 };
        var builderHeading = new StackPanel { Spacing = 4 };
        AutomationProperties.SetHeadingLevel(_title, AutomationHeadingLevel.Level3);
        builderHeading.Children.Add(_title);
        builderHeading.Children.Add(_help);
        builderBody.Children.Add(builderHeading);
        builderBody.Children.Add(_fields);
        var previewBody = new StackPanel { Spacing = 8 };
        previewBody.Children.Add(StrongText("Rule preview"));
        _preview.IsTextSelectionEnabled = true;
        previewBody.Children.Add(_preview);
        builderBody.Children.Add(new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Padding = new Thickness(16), CornerRadius = new CornerRadius(8), Child = previewBody
        });
        var exampleBody = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 4) };
        exampleBody.Children.Add(_example);
        exampleBody.Children.Add(_exampleResult);
        builderBody.Children.Add(new Expander
        {
            Header = "Try a name or path", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = exampleBody
        });
        builderBody.Children.Add(_error);
        _save.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_save);
        actions.Children.Add(_cancel);
        actions.SizeChanged += (_, args) =>
        {
            var size = new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            _save.Measure(size); _cancel.Measure(size);
            var stacked = args.NewSize.Width < _save.DesiredSize.Width + _cancel.DesiredSize.Width + actions.Spacing;
            actions.Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
            _save.HorizontalAlignment = _cancel.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        };
        builderBody.Children.Add(actions);
        _builder = SourceImportDialog.Surface(builderBody);
        _builder.Visibility = Visibility.Collapsed;
        _root.Children.Add(_builder);
        Content = _root;

        foreach (var text in new[] { "Are exactly", "Contain", "Start with", "End with" }) _match.Items.Add(text);
        _value.TextChanged += (_, _) => UpdatePreview();
        _match.SelectionChanged += (_, _) => UpdatePreview();
        _example.TextChanged += (_, _) => UpdatePreview();
        _save.Click += (_, _) => SaveRule();
        _cancel.Click += (_, _) => CloseBuilder();
        RenderRows();
    }

    private void AddChoice(MenuFlyout menu, string text, string glyph, EditorKind kind)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = FluentIcons.FromGlyph(glyph, 24) };
        item.Click += (_, _) => OpenBuilder(kind);
        menu.Items.Add(item);
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        _empty.Visibility = _rules.Count == 0 && !IsEditing ? Visibility.Visible : Visibility.Collapsed;
        _rows.Visibility = _rules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var rule in _rules)
        {
            var title = FriendlyPattern(rule.Pattern);
            var row = new Grid { ColumnSpacing = 16, MinHeight = 40 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var icon = FluentIcons.Create(rule.Pattern.Contains('/') ? "folder" : "filter", 24);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
            var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(StrongText(title));
            copy.Children.Add(SourceImportDialog.Text(rule.Pattern.Contains('/') ? "Relative path · This transfer" : "Matching names · This transfer", true));
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new ToggleSwitch { IsOn = rule.Enabled, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = !IsEditing };
            AutomationProperties.SetName(toggle, "Exclude " + title);
            toggle.Toggled += (_, _) => { rule.Enabled = toggle.IsOn; OnStateChanged(); };
            actions.Children.Add(toggle);
            var more = new Button
            {
                Content = CommandIcon("\uE712", 16), Width = 36, Height = 36, Padding = new Thickness(8),
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SubtleButtonStyle"], IsEnabled = !IsEditing
            };
            AutomationProperties.SetName(more, "Options for " + title);
            ToolTipService.SetToolTip(more, "Options for " + title);
            var menu = new MenuFlyout();
            FluentIconMotion.Attach(menu);
            var edit = new MenuFlyoutItem { Text = "Edit exclusion", Icon = FluentIcons.FromGlyph("\uE70F", 20) };
            edit.Click += (_, _) => OpenExisting(rule);
            var remove = new MenuFlyoutItem { Text = "Remove exclusion", Icon = FluentIcons.FromGlyph("\uE74D", 20) };
            remove.Click += (_, _) =>
            {
                if (IsEditing) return;
                _rules.Remove(rule); RenderRows(); OnStateChanged(); _add.Focus(FocusState.Programmatic);
            };
            menu.Items.Add(edit); menu.Items.Add(remove);
            more.Flyout = menu;
            actions.Children.Add(more);
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            row.SizeChanged += (_, args) =>
            {
                var stacked = args.NewSize.Width < 440 * _textSettings.TextScaleFactor;
                Grid.SetColumnSpan(copy, stacked ? 2 : 1);
                Grid.SetRow(actions, stacked ? 1 : 0);
                Grid.SetColumn(actions, stacked ? 1 : 2);
                Grid.SetColumnSpan(actions, stacked ? 2 : 1);
                actions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                row.RowSpacing = stacked ? 12 : 0;
            };
            _rows.Children.Add(SourceImportDialog.Surface(row));
        }
    }

    private void OpenExisting(Rule rule)
    {
        var pattern = rule.Pattern;
        if (IsFolderPattern(pattern))
            OpenBuilder(EditorKind.Folder, rule, pattern[..^2]);
        else if (pattern.StartsWith("*.", StringComparison.Ordinal) && !pattern[2..].Any(c => c is '*' or '?' or '/' or '\\'))
            OpenBuilder(EditorKind.Type, rule, pattern[2..]);
        else if (!pattern.Any(c => c is '*' or '?' or '/' or '\\'))
            OpenBuilder(EditorKind.Name, rule, pattern);
        else if (!pattern.Contains('/') && !pattern.Contains('\\') && !pattern.Contains('?') && pattern.Trim('*') is { Length: > 0 } literal && !literal.Contains('*'))
            OpenBuilder(EditorKind.Name, rule, literal, pattern.StartsWith('*') && pattern.EndsWith('*') ? 1 : pattern.EndsWith('*') ? 2 : 3);
        else OpenBuilder(EditorKind.Advanced, rule, pattern);
    }

    private void OpenBuilder(EditorKind kind, Rule? editing = null, string value = "", int match = 0)
    {
        if (IsEditing) return;
        _refreshing = true;
        _kind = kind; _editing = editing;
        _fields.Children.Clear();
        // A field may still belong to the paired grid used for the previous draft.
        if (_value.Parent is Panel oldValueParent) oldValueParent.Children.Remove(_value);
        if (_match.Parent is Panel oldMatchParent) oldMatchParent.Children.Remove(_match);
        _value.Text = value; _match.SelectedIndex = match; _example.Text = "";
        _error.IsOpen = false; _error.Visibility = Visibility.Collapsed;
        _title.Text = (editing is null ? "Add " : "Edit ") + (kind switch
        {
            EditorKind.Type => "a file type", EditorKind.Name => "a name rule", EditorKind.Path => "a file path",
            EditorKind.Folder => "a folder path", _ => "an advanced pattern"
        });
        _help.Text = kind switch
        {
            EditorKind.Type => "Skip matching extensions anywhere in the source. Folder names with the same ending also match.",
            EditorKind.Name => "Match file or folder names anywhere in the source.",
            EditorKind.Path => "Skip one file at this exact path, relative to the source folder. Use / between folders.",
            EditorKind.Folder => "Skip all files inside this folder and its subfolders, relative to the source folder.",
            _ => "Match source names or relative paths. Patterns with / match the complete relative path; * can span folder separators."
        };
        _value.Header = kind switch { EditorKind.Type => "File extension", EditorKind.Name => "Name or text", EditorKind.Path => "Relative file path", EditorKind.Folder => "Relative folder path", _ => "Pattern" };
        _value.PlaceholderText = kind switch { EditorKind.Type => "For example, tmp", EditorKind.Name => "For example, cache", EditorKind.Path => "For example, Archive/old-report.pdf", EditorKind.Folder => "For example, Archive/cache", _ => "For example, Archive/*.tmp" };
        _value.MaxLength = kind is EditorKind.Type or EditorKind.Name ? 255 : 4096;
        if (kind == EditorKind.Name) _fields.Children.Add(SourceImportDialog.PairedContent(_match, _value, 260));
        else _fields.Children.Add(_value);
        if (kind == EditorKind.Advanced)
        {
            var addPart = SourceImportDialog.ActionButton("Add matching part");
            var menu = new MenuFlyout();
            foreach (var (label, token) in new[] { ("Any text (*)", "*"), ("One character (?)", "?"), ("Folder separator (/)", "/") })
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) =>
                {
                    var start = _value.SelectionStart;
                    _value.Text = _value.Text.Remove(start, _value.SelectionLength).Insert(start, token);
                    _value.Focus(FocusState.Programmatic); _value.Select(start + token.Length, 0);
                };
                menu.Items.Add(item);
            }
            addPart.Flyout = menu;
            _fields.Children.Add(addPart);
        }
        _save.Content = editing is null ? "Add rule" : "Save changes";
        _builder.Visibility = Visibility.Visible; _add.IsEnabled = false;
        _refreshing = false;
        RenderRows(); UpdatePreview(); OnStateChanged();
        _value.Focus(FocusState.Programmatic);
        _builder.StartBringIntoView();
    }

    private string BuildPattern()
    {
        var value = _value.Text;
        string pattern;
        if (_kind == EditorKind.Type)
        {
            value = value.Trim().TrimStart('.');
            ValidateNameText(value);
            pattern = "*." + value;
        }
        else if (_kind == EditorKind.Name)
        {
            ValidateNameText(value);
            pattern = _match.SelectedIndex switch { 1 => "*" + value + "*", 2 => value + "*", 3 => "*" + value, _ => value };
        }
        else if (_kind is EditorKind.Path or EditorKind.Folder)
        {
            pattern = PathRules.ValidateRelative(value.Trim().Replace('\\', '/').TrimEnd('/'));
            if (pattern.Length == 0) throw new InvalidDataException("Enter a path inside the source folder.");
            if (pattern.IndexOfAny(['*', '?']) >= 0) throw new InvalidDataException("Use Advanced pattern for wildcards.");
            if (_kind == EditorKind.Folder) pattern += "/*";
        }
        else pattern = value;
        // Transfer creation trims patterns too; preview exactly what will run.
        pattern = pattern.Trim();
        // Keep the existing transfer plan bounds and legacy pattern syntax. In
        // particular, do not reinterpret ** with guided backup rule semantics.
        if (string.IsNullOrWhiteSpace(pattern)) throw new InvalidDataException("Enter a name, path, or matching part.");
        if (pattern.Length > 4096 || pattern.Contains("..") || pattern.Contains(':'))
            throw new InvalidDataException("Use a relative name or pattern of up to 4096 characters, without .. or a drive letter.");
        if (pattern.Any(char.IsControl)) throw new InvalidDataException("Enter one rule without line breaks or control characters.");
        if (_editing is null && _rules.Count >= 4096) throw new InvalidDataException("This transfer already has the maximum of 4096 exclusions. Remove a rule before adding another.");
        return pattern;
    }

    private static void ValidateNameText(string value)
    {
        if (value.Length == 0) throw new InvalidDataException("Enter the extension or name text to match.");
        if (value.Any(c => c is '*' or '?' or '/' or '\\' or '<' or '>' or ':' or '"' or '|' || char.IsControl(c)))
            throw new InvalidDataException("Enter ordinary name text. Use Folder path for folders or Advanced pattern for wildcards.");
    }

    private void UpdatePreview()
    {
        if (_refreshing || !IsEditing) return;
        _error.IsOpen = false; _error.Visibility = Visibility.Collapsed;
        try
        {
            var pattern = BuildPattern();
            _preview.Text = FriendlyPattern(pattern) + Environment.NewLine + "Pattern: " + pattern;
            _save.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(_example.Text)) _exampleResult.Text = "Try a source name or relative path to check this rule.";
            else
            {
                try
                {
                    var sample = PathRules.ValidateRelative(_example.Text.Replace('\\', '/'));
                    _exampleResult.Text = PathRules.IsExcluded(sample, [pattern])
                        ? "This example would be excluded." : "This example would be included by this rule.";
                }
                catch (InvalidDataException error) { _exampleResult.Text = error.Message; }
            }
        }
        catch (InvalidDataException error)
        {
            _save.IsEnabled = false;
            _preview.Text = error.Message;
            _exampleResult.Text = "Complete the rule to try an example.";
        }
    }

    private void SaveRule()
    {
        if (!IsEditing) return;
        try
        {
            var pattern = BuildPattern();
            if (_editing is null) _rules.Add(new Rule(pattern));
            else _editing.Pattern = pattern;
            CloseBuilder();
        }
        catch (InvalidDataException error)
        {
            _error.Message = error.Message; _error.IsOpen = true; _error.Visibility = Visibility.Visible;
        }
    }

    private void CloseBuilder()
    {
        _builder.Visibility = Visibility.Collapsed; _editing = null; _add.IsEnabled = true;
        RenderRows(); OnStateChanged(); _add.Focus(FocusState.Programmatic);
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    private static FontIcon CommandIcon(string glyph, double size) => new() { Glyph = glyph, FontSize = size };
    private static TextBlock StrongText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"]
    };

    private static string FriendlyPattern(string pattern)
    {
        if (IsFolderPattern(pattern)) return "Files inside “" + pattern[..^2] + "”";
        if (pattern.StartsWith("*.", StringComparison.Ordinal) && !pattern[2..].Any(c => c is '*' or '?' or '/' or '\\'))
            return $"{pattern[2..].ToUpperInvariant()} file type";
        if (!pattern.Any(c => c is '*' or '?' or '/' or '\\')) return $"Name is “{pattern}”";
        if (!pattern.Contains('/') && !pattern.Contains('\\') && !pattern.Contains('?'))
        {
            var literal = pattern.Trim('*');
            if (literal.Length > 0 && !literal.Contains('*'))
            {
                if (pattern.StartsWith('*') && pattern.EndsWith('*')) return $"Name contains “{literal}”";
                if (pattern.EndsWith('*')) return $"Name starts with “{literal}”";
                if (pattern.StartsWith('*')) return $"Name ends with “{literal}”";
            }
        }
        return "Path pattern: " + pattern;
    }

    private static bool IsFolderPattern(string pattern) => pattern.Length > 2 && pattern.EndsWith("/*", StringComparison.Ordinal) &&
        !pattern[..^2].Any(c => c is '*' or '?' or '\\');
}
