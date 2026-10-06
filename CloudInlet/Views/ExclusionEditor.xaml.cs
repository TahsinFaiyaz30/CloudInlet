using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CloudInlet.Views;

/// <summary>Builds exclusion rules without requiring users to write wildcard expressions.</summary>
public sealed partial class ExclusionEditor : UserControl
{
    private enum EditorKind { Selection, Type, Name, Advanced }
    private enum RuleKind { Selected, Guided, Legacy }
    private enum TokenKind { Literal, AnyText, AnyCharacter, Subfolders, Separator }
    private sealed record PatternToken(TokenKind Kind, string Text = "")
    {
        public string Pattern => Kind switch
        {
            TokenKind.Literal => Text, TokenKind.AnyText => "*", TokenKind.AnyCharacter => "?",
            TokenKind.Subfolders => "**", _ => "/"
        };
        public string Description => Kind switch
        {
            TokenKind.Literal => $"Text: {Text}", TokenKind.AnyText => "Any text (*)",
            TokenKind.AnyCharacter => "One character (?)", TokenKind.Subfolders => "Any number of subfolders (**)",
            _ => "Folder separator (/)"
        };
    }
    private sealed record RuleReference(RuleKind Kind, object Value);
    private sealed record RuleScope(string Label, string? Root = null, string? Directory = null)
    {
        public override string ToString() => Label;
    }

    private AppSettings _settings = new();
    private bool _presentationOnly;
    private bool _refreshing;
    private bool _saving;
    private bool _picking;
    private EditorKind _kind;
    private RuleReference? _editing;
    private readonly List<PatternToken> _tokens = [];
    private List<SelectedExclusion> _selections = [];
    private bool _selectionIsFolder;
    private readonly List<(Control Control, bool WasEnabled)> _disabledBuilderControls = [];
    private readonly List<(Control Control, bool WasEnabled)> _disabledPickerControls = [];
    private readonly List<StackPanel> _tokenActions = [];
    private readonly List<(RuleReference Reference, ToggleSwitch Toggle, Button More)> _rowActions = [];
    private int _builderGeneration;
    private string _lastTestedPath = "";

    public nint OwnerWindowHandle { get; set; }
    public Func<PreferenceUpdate, Task>? SaveChangesAsync { get; set; }

    public ExclusionEditor()
    {
        InitializeComponent();
        // A chooser can outlive navigation away from this view. Its result must
        // never land in a subsequently reopened or different exclusion draft.
        Unloaded += (_, _) => _builderGeneration++;
        foreach (var bar in new[] { ErrorBar, BuilderError })
            bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (sender, _) =>
            {
                var changed = (InfoBar)sender;
                changed.Visibility = changed.IsOpen ? Visibility.Visible : Visibility.Collapsed;
            });
        RenderRows();
    }

    public void SetSettings(AppSettings settings, bool presentationOnly = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var previous = _settings;
        _settings = CloneExclusions(settings);
        _presentationOnly = presentationOnly;
        // A background snapshot refresh must never replace a rule the user is building.
        if (!SamePresentation(previous, _settings)) RenderRows();
    }

    private static bool SamePresentation(AppSettings first, AppSettings second) =>
        first.RootPath == second.RootPath && first.CustomBackups.SequenceEqual(second.CustomBackups) &&
        first.Exclusions.SequenceEqual(second.Exclusions) && first.DisabledLegacyExclusions.SequenceEqual(second.DisabledLegacyExclusions) &&
        first.SelectedExclusions.SequenceEqual(second.SelectedExclusions) && first.GuidedExclusions.SequenceEqual(second.GuidedExclusions);

    private static AppSettings CloneExclusions(AppSettings settings) => settings with
    {
        Exclusions = [.. settings.Exclusions], DisabledLegacyExclusions = [.. settings.DisabledLegacyExclusions],
        SelectedExclusions = [.. settings.SelectedExclusions], GuidedExclusions = [.. settings.GuidedExclusions]
    };

    private void RenderRows()
    {
        if (RulesPanel is null) return;
        RulesPanel.Children.Clear();
        _rowActions.Clear();
        foreach (var selected in _settings.SelectedExclusions)
            AddRow(new(RuleKind.Selected, selected), selected.RelativePath.Split('/')[^1],
                $"{(selected.IsFolder ? "Folder and everything inside" : "Selected file")} · {ScopeDescription(selected.RootPath, null)}\n{selected.RelativePath}",
                selected.IsFolder ? "\uE8B7" : "\uE8A5", selected.Enabled);
        foreach (var guided in _settings.GuidedExclusions)
            AddRow(new(RuleKind.Guided, guided), FriendlyPattern(guided.Pattern),
                $"{TargetDescription(guided.Target)} · {ScopeDescription(guided.RootPath, guided.RelativeDirectory)}",
                "\uE8D2", guided.Enabled);
        foreach (var legacy in _settings.Exclusions)
            AddRow(new(RuleKind.Legacy, legacy), FriendlyPattern(legacy),
                "Existing pattern · All backup folders", "\uE8D2",
                !_settings.DisabledLegacyExclusions.Contains(legacy, StringComparer.OrdinalIgnoreCase));
        EmptyPanel.Visibility = RulesPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RulesPanel.Visibility = RulesPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddRow(RuleReference reference, string title, string detail, string glyph, bool enabled)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Style = (Style)global::Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
        copy.Children.Add(new TextBlock { Text = detail, Style = (Style)Resources["ExclusionSecondary"] });
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var canChange = !_saving && !_picking && BuilderPanel.Visibility != Visibility.Visible;
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = canChange };
        AutomationProperties.SetName(toggle, $"Exclude {title}");
        toggle.Toggled += async (_, _) =>
        {
            if (_saving || _picking) return;
            await ChangeEnabledAsync(reference, toggle.IsOn);
        };
        actions.Children.Add(toggle);
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 16 }, IsEnabled = canChange };
        AutomationProperties.SetName(more, $"Options for {title}");
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit exclusion", Icon = new FontIcon { Glyph = "\uE70F" } };
        edit.Click += (_, _) => OpenExisting(reference);
        var remove = new MenuFlyoutItem { Text = "Remove exclusion", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) => await RemoveAsync(reference);
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        more.Flyout = menu;
        _rowActions.Add((reference, toggle, more));
        actions.Children.Add(more);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        // At narrow widths the actions move below the text instead of squeezing it.
        bool? previousStacked = null;
        row.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < 440;
            if (previousStacked == stacked) return;
            previousStacked = stacked;
            row.RowDefinitions.Clear();
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            if (stacked) row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(actions, stacked ? 1 : 0);
            Grid.SetColumn(actions, stacked ? 1 : 2);
            Grid.SetColumnSpan(copy, stacked ? 2 : 1);
            actions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            actions.Margin = stacked ? new(0, 12, 0, 0) : new(0);
        };
        RulesPanel.Children.Add(new Border { Style = (Style)Resources["ExclusionSurface"], Padding = new(20, 16, 20, 16), Child = row });
    }

    private string ScopeDescription(string? root, string? directory)
    {
        if (root is null) return "All backup folders";
        var label = _settings.CustomBackups.FirstOrDefault(folder => PathEquals(folder.SourcePath, root))?.Name ??
            (PathEquals(_settings.RootPath, root) ? "CloudInlet folder" : root);
        return directory is null ? label : $"{label} / {directory}";
    }

    private static bool PathEquals(string first, string second) =>
        Path.TrimEndingDirectorySeparator(first).Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    private static string TargetDescription(ExclusionTarget target) => target switch
    {
        ExclusionTarget.Files => "Files", ExclusionTarget.Folders => "Folders and everything inside", _ => "Files and folders"
    };

    private static string FriendlyPattern(string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal) && !pattern[2..].Any(c => c is '*' or '?' or '/')) return $"{pattern[2..].ToUpperInvariant()} files";
        if (!pattern.Any(c => c is '*' or '?' or '/')) return $"Name is “{pattern}”";
        if (!pattern.Contains('/') && !pattern.Contains('?'))
        {
            var literal = pattern.Trim('*');
            if (literal.Length > 0 && !literal.Contains('*'))
            {
                if (pattern.StartsWith('*') && pattern.EndsWith('*')) return $"Name contains “{literal}”";
                if (pattern.EndsWith('*')) return $"Name starts with “{literal}”";
                if (pattern.StartsWith('*')) return $"Name ends with “{literal}”";
            }
        }
        return $"Pattern: {pattern}";
    }

    private void StartBuilder(EditorKind kind, RuleReference? editing = null)
    {
        if (_saving || _picking || BuilderPanel.Visibility == Visibility.Visible) return;
        var generation = ++_builderGeneration;
        _refreshing = true;
        try
        {
            _kind = kind;
            _editing = editing;
            _tokens.Clear();
            _selections = [];
            _selectionIsFolder = false;
            ExtensionBox.Text = "";
            NameBox.Text = "";
            LiteralBox.Text = "";
            TestPathBox.Text = "";
            NameMatchBox.SelectedIndex = 0;
            TargetBox.SelectedIndex = 0;
            TestKindBox.SelectedIndex = 0;
            TestPanel.IsExpanded = false;
            BuilderError.IsOpen = false;
            ErrorBar.IsOpen = false;
            ScopeBox.Items.Clear();
            ScopeBox.Items.Add(new RuleScope("All backup folders"));
            ScopeBox.Items.Add(new RuleScope("CloudInlet folder", NormalizeRoot(_settings.RootPath)));
            foreach (var folder in _settings.CustomBackups)
                ScopeBox.Items.Add(new RuleScope(folder.Name, NormalizeRoot(folder.SourcePath)));
            ScopeBox.SelectedIndex = 0;
            SelectionPanel.Visibility = kind == EditorKind.Selection ? Visibility.Visible : Visibility.Collapsed;
            TypePanel.Visibility = kind == EditorKind.Type ? Visibility.Visible : Visibility.Collapsed;
            NamePanel.Visibility = kind == EditorKind.Name ? Visibility.Visible : Visibility.Collapsed;
            AdvancedPanel.Visibility = kind == EditorKind.Advanced ? Visibility.Visible : Visibility.Collapsed;
            ScopePanel.Visibility = kind == EditorKind.Selection ? Visibility.Collapsed : Visibility.Visible;
            TestPanel.Visibility = kind == EditorKind.Selection ? Visibility.Collapsed : Visibility.Visible;
            TargetBox.Visibility = kind == EditorKind.Type ? Visibility.Collapsed : Visibility.Visible;
            BuilderTitle.Text = (editing is null ? "Add " : "Edit ") + (kind switch
            {
                EditorKind.Selection => "selected files or folder", EditorKind.Type => "file type",
                EditorKind.Name => "name rule", _ => "advanced pattern"
            });
            BuilderHelp.Text = kind switch
            {
                EditorKind.Selection => "Only these items in this backup folder will be excluded.",
                EditorKind.Type => "Choose an example file or enter an extension. No wildcard syntax is needed.",
                EditorKind.Name => "Enter ordinary text and choose how the name should match.",
                _ => "Choose matching parts to describe a name or a path. You do not need to write a wildcard expression."
            };
            BuilderPanel.Visibility = Visibility.Visible;
        }
        finally { _refreshing = false; }
        RenderTokens();
        UpdatePreview();
        AddButton.IsEnabled = false;
        RenderRows();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (BuilderPanel.Visibility != Visibility.Visible || generation != _builderGeneration) return;
            BuilderPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            var focus = kind switch { EditorKind.Type => (Control)ExtensionBox, EditorKind.Name => NameBox,
                EditorKind.Advanced => LiteralBox, _ => ChangeSelectionButton };
            focus.Focus(FocusState.Programmatic);
        });
    }

    private void OpenExisting(RuleReference reference)
    {
        if (_saving || _picking || BuilderPanel.Visibility == Visibility.Visible) return;
        if (reference.Kind == RuleKind.Selected)
        {
            StartBuilder(EditorKind.Selection, reference);
            _selections = [(SelectedExclusion)reference.Value];
            _selectionIsFolder = _selections[0].IsFolder;
            UpdatePreview();
            return;
        }
        StartBuilder(EditorKind.Advanced, reference);
        var guided = reference.Value as GuidedExclusion;
        var pattern = guided?.Pattern ?? (string)reference.Value;
        _tokens.AddRange(ParseTokens(pattern));
        if (guided is not null)
        {
            TargetBox.SelectedIndex = (int)guided.Target;
            SelectScope(new(ScopeDescription(guided.RootPath, guided.RelativeDirectory), guided.RootPath, guided.RelativeDirectory));
        }
        else
        {
            TargetBox.SelectedIndex = (int)ExclusionTarget.All;
            BuilderHelp.Text = "Saving converts this existing rule to guided matching. Any text and one character stay within a name; use the subfolders part to match across folders. A path rule may match differently after conversion. Review the example before saving. Cancel keeps its original behavior.";
        }
        RenderTokens();
        UpdatePreview();
    }

    private static IEnumerable<PatternToken> ParseTokens(string pattern)
    {
        var literal = new System.Text.StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character is not ('*' or '?' or '/')) { literal.Append(character); continue; }
            if (literal.Length > 0) { yield return new(TokenKind.Literal, literal.ToString()); literal.Clear(); }
            if (character == '*' && (index == 0 || pattern[index - 1] == '/') && index + 1 < pattern.Length && pattern[index + 1] == '*' &&
                (index + 2 == pattern.Length || pattern[index + 2] == '/'))
            {
                yield return new(TokenKind.Subfolders);
                index++;
            }
            else yield return new(character switch { '*' => TokenKind.AnyText, '?' => TokenKind.AnyCharacter, _ => TokenKind.Separator });
        }
        if (literal.Length > 0) yield return new(TokenKind.Literal, literal.ToString());
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private void SelectScope(RuleScope scope)
    {
        var existing = ScopeBox.Items.OfType<RuleScope>().FirstOrDefault(item => item.Root == scope.Root && item.Directory == scope.Directory);
        if (existing is null) { ScopeBox.Items.Add(scope); existing = scope; }
        ScopeBox.SelectedItem = existing;
    }

    private void RenderTokens()
    {
        TokensPanel.Children.Clear();
        _tokenActions.Clear();
        for (var i = 0; i < _tokens.Count; i++)
        {
            var index = i;
            var token = _tokens[i];
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = token.Description, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            AddTokenAction(actions, "\uE70E", $"Move part {i + 1} earlier", i > 0, () => MoveToken(index, -1));
            AddTokenAction(actions, "\uE70D", $"Move part {i + 1} later", i < _tokens.Count - 1, () => MoveToken(index, 1));
            AddTokenAction(actions, "\uE711", $"Remove part {i + 1}", true, () =>
            {
                _tokens.RemoveAt(index);
                RenderTokens();
                UpdatePreview();
                FocusToken(Math.Min(index, _tokens.Count - 1), 2);
            });
            _tokenActions.Add(actions);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            TokensPanel.Children.Add(new Border { Child = grid, Style = (Style)Resources["ExclusionTokenSurface"] });
        }
        if (_tokens.Count == 0) TokensPanel.Children.Add(new TextBlock { Text = "Add your first matching part below.", Style = (Style)Resources["ExclusionSecondary"] });
    }

    private static void AddTokenAction(Panel parent, string glyph, string label, bool enabled, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, IsEnabled = enabled };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }

    private void MoveToken(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= _tokens.Count) return;
        (_tokens[index], _tokens[target]) = (_tokens[target], _tokens[index]);
        RenderTokens();
        UpdatePreview();
        FocusToken(target, offset < 0 ? 0 : 1);
    }

    private void FocusToken(int index, int action)
    {
        if (index < 0 || index >= _tokenActions.Count) { LiteralBox.Focus(FocusState.Programmatic); return; }
        var buttons = _tokenActions[index].Children.OfType<Button>().ToArray();
        (buttons[action].IsEnabled ? buttons[action] : buttons[^1]).Focus(FocusState.Programmatic);
    }

    private void AddLiteral_Click(object sender, RoutedEventArgs args)
    {
        if (_saving || _picking) return;
        try
        {
            ValidateLiteral(LiteralBox.Text);
            _tokens.Add(new(TokenKind.Literal, LiteralBox.Text));
            LiteralBox.Text = "";
            BuilderError.IsOpen = false;
            RenderTokens();
            UpdatePreview();
        }
        catch (Exception error) { ShowError(error.Message, builder: true); }
    }

    private void AddToken_Click(object sender, RoutedEventArgs args)
    {
        if (_saving || _picking) return;
        if (sender is not MenuFlyoutItem { Tag: string kind }) return;
        _tokens.Add(new(kind switch { "text" => TokenKind.AnyText, "character" => TokenKind.AnyCharacter,
            "subfolders" => TokenKind.Subfolders, _ => TokenKind.Separator }));
        RenderTokens();
        UpdatePreview();
    }

    private static void ValidateLiteral(string text)
    {
        if (text.Length == 0) throw new InvalidDataException("Enter some ordinary text first.");
        if (text.Any(c => c is '*' or '?' or '/' or '\\' or '<' or '>' or ':' or '"' or '|' || char.IsControl(c)))
            throw new InvalidDataException("Enter ordinary name text. Add matching parts and folder separators with the buttons.");
    }

    private GuidedExclusion BuildGuidedRule()
    {
        var pattern = _kind switch
        {
            EditorKind.Type => BuildExtensionPattern(),
            EditorKind.Name => BuildNamePattern(),
            _ => string.Concat(_tokens.Select(token => token.Pattern))
        };
        if (string.IsNullOrWhiteSpace(pattern)) throw new InvalidDataException("Add a name or a matching part first.");
        if (_kind == EditorKind.Advanced)
        {
            for (var i = 0; i < _tokens.Count; i++)
                if (_tokens[i].Kind == TokenKind.Subfolders &&
                    (i > 0 && _tokens[i - 1].Kind != TokenKind.Separator || i + 1 < _tokens.Count && _tokens[i + 1].Kind != TokenKind.Separator))
                    throw new InvalidDataException("Put folder separators before and after an ‘any number of subfolders’ part, or place it at the beginning or end.");
        }
        var scope = ScopeBox.SelectedItem as RuleScope ?? new("All backup folders");
        var enabled = _editing?.Value switch
        {
            GuidedExclusion existing => existing.Enabled,
            string legacy => !_settings.DisabledLegacyExclusions.Contains(legacy, StringComparer.OrdinalIgnoreCase), _ => true
        };
        var rule = new GuidedExclusion(pattern, _kind == EditorKind.Type ? ExclusionTarget.Files : (ExclusionTarget)Math.Max(0, TargetBox.SelectedIndex),
            scope.Root, scope.Directory, enabled);
        PathRules.ValidateSettings(_settings with { GuidedExclusions = [rule] });
        return rule;
    }

    private string BuildExtensionPattern()
    {
        var extension = ExtensionBox.Text.Trim().TrimStart('.');
        if (extension.Length == 0) throw new InvalidDataException("Choose an example file, or enter a file extension such as jpg.");
        ValidateLiteral(extension);
        return "*." + extension;
    }

    private string BuildNamePattern()
    {
        var name = NameBox.Text;
        ValidateLiteral(name);
        return NameMatchBox.SelectedIndex switch { 1 => "*" + name + "*", 2 => name + "*", 3 => "*" + name, _ => name };
    }

    private void UpdatePreview()
    {
        if (_refreshing || PreviewSummary is null || BuilderPanel.Visibility != Visibility.Visible) return;
        if (_kind == EditorKind.Selection)
        {
            SelectionSummary.Text = _selections.Count == 0 ? "Choose files or a folder inside a configured backup." :
                string.Join("\n", _selections.Select(selection => $"{selection.RelativePath} · {ScopeDescription(selection.RootPath, null)}"));
            PreviewSummary.Text = _selections.Count == 0 ? "No items selected." :
                $"Exclude {_selections.Count} selected {(_selections.Count == 1 ? (_selections[0].IsFolder ? "folder and everything inside" : "file") : "files")} in their current backup folders.";
            PreviewPattern.Text = "Selections are literal paths. Similar names in other folders stay included.";
            SaveButton.IsEnabled = !_saving && !_picking && _selections.Count > 0;
            return;
        }
        var scope = ScopeBox.SelectedItem as RuleScope;
        ScopeFolderSummary.Visibility = scope?.Directory is null ? Visibility.Collapsed : Visibility.Visible;
        ScopeFolderSummary.Text = scope?.Directory is null ? "" : $"Inside: {scope.Directory}";
        try
        {
            var rule = BuildGuidedRule();
            var condition = _kind == EditorKind.Type
                ? $"with names ending in “.{ExtensionBox.Text.Trim().TrimStart('.')}”"
                : NameMatchBox.SelectedIndex switch
                {
                    1 => $"with names containing “{NameBox.Text}”", 2 => $"with names starting with “{NameBox.Text}”",
                    3 => $"with names ending in “{NameBox.Text}”", _ => $"named “{NameBox.Text}”"
                };
            var target = rule.Target == ExclusionTarget.Folders ? "folders" : TargetDescription(rule.Target).ToLowerInvariant();
            var contents = rule.Target == ExclusionTarget.Files ? "" : " Matching folders include everything inside them.";
            PreviewSummary.Text = _kind == EditorKind.Advanced
                ? $"Exclude {TargetDescription(rule.Target).ToLowerInvariant()} in {ScopeDescription(rule.RootPath, rule.RelativeDirectory)}. Match these parts in order: {string.Join(" → ", _tokens.Select(token => token.Description))}."
                : $"Exclude {target} {condition} in {ScopeDescription(rule.RootPath, rule.RelativeDirectory)}.{contents}";
            PreviewPattern.Text = "Generated pattern: " + rule.Pattern;
            SaveButton.IsEnabled = !_saving && !_picking;
            UpdateTest(rule);
        }
        catch (Exception error)
        {
            PreviewSummary.Text = error.Message;
            PreviewPattern.Text = _kind == EditorKind.Advanced ? "Pattern so far: " + string.Concat(_tokens.Select(token => token.Pattern)) : "";
            TestResult.Text = "Finish the rule to try a name or path.";
            SaveButton.IsEnabled = false;
        }
    }

    private void UpdateTest(GuidedExclusion rule)
    {
        var path = TestPathBox.Text.Trim().Replace('\\', '/');
        _lastTestedPath = path;
        if (path.Length == 0) { TestResult.Text = "Enter an example path. No file is opened or changed."; return; }
        try
        {
            PathRules.ValidateRelative(path);
            var scopedPath = rule.RelativeDirectory is null ? path : rule.RelativeDirectory + "/" + path;
            var settings = _settings with { RootPath = rule.RootPath ?? _settings.RootPath, Exclusions = [], DisabledLegacyExclusions = [],
                SelectedExclusions = [], GuidedExclusions = [rule with { Enabled = true }] };
            var excluded = PathRules.IsExcluded(scopedPath, settings, TestKindBox.SelectedIndex == 1);
            TestResult.Text = excluded ? "This example would be excluded." : "This example would stay included.";
        }
        catch (Exception error) { TestResult.Text = error.Message; }
    }

    private void Draft_TextChanged(object sender, TextChangedEventArgs args) => UpdatePreview();
    private void Draft_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdatePreview();
    private void AddType_Click(object sender, RoutedEventArgs args) => StartBuilder(EditorKind.Type);
    private void AddName_Click(object sender, RoutedEventArgs args) => StartBuilder(EditorKind.Name);
    private void AddAdvanced_Click(object sender, RoutedEventArgs args) => StartBuilder(EditorKind.Advanced);
    private async void AddFiles_Click(object sender, RoutedEventArgs args)
    {
        if (_saving || _picking || BuilderPanel.Visibility == Visibility.Visible) return;
        StartBuilder(EditorKind.Selection);
        await ChooseSelectionsAsync(false);
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_saving || _picking || BuilderPanel.Visibility == Visibility.Visible) return;
        StartBuilder(EditorKind.Selection);
        await ChooseSelectionsAsync(true);
    }
    private async void ChangeSelection_Click(object sender, RoutedEventArgs args) => await ChooseSelectionsAsync(_selectionIsFolder);

    private void EnsurePickerAvailable()
    {
        if (OwnerWindowHandle == 0) throw new InvalidOperationException("The window is not ready to open a file picker. Try again after the window opens.");
    }

    private bool TryBeginPicker(out int generation)
    {
        generation = _builderGeneration;
        if (_saving || _picking || BuilderPanel.Visibility != Visibility.Visible) return false;
        EnsurePickerAvailable();
        _picking = true;
        _disabledPickerControls.Clear();
        CollectBuilderControlStates(BuilderPanel, _disabledPickerControls);
        foreach (var (control, _) in _disabledPickerControls)
            if (!ReferenceEquals(control, CancelButton)) control.IsEnabled = false;
        BuilderError.IsOpen = false;
        UpdatePickerControls();
        UpdatePreview();
        return true;
    }

    private bool IsCurrentDraft(int generation) => generation == _builderGeneration && IsLoaded &&
        BuilderPanel.Visibility == Visibility.Visible;

    private void EndPicker(int generation, Control focus)
    {
        _picking = false;
        foreach (var (control, wasEnabled) in _disabledPickerControls) control.IsEnabled = wasEnabled;
        _disabledPickerControls.Clear();
        UpdatePickerControls();
        UpdatePreview();
        if (IsCurrentDraft(generation)) focus.Focus(FocusState.Programmatic);
    }

    private void UpdatePickerControls()
    {
        var available = !_saving && !_picking;
        ChangeSelectionButton.IsEnabled = available;
        ExampleFileButton.IsEnabled = available;
        ScopeFolderButton.IsEnabled = available;
        AddButton.IsEnabled = available && BuilderPanel.Visibility != Visibility.Visible;
        RenderRows();
    }

    private async Task ChooseSelectionsAsync(bool folder)
    {
        var generation = _builderGeneration;
        var opened = false;
        try
        {
            if (!TryBeginPicker(out generation)) return;
            opened = true;
            _selectionIsFolder = folder;
            List<SelectedExclusion> chosen = [];
            if (folder)
            {
                var selected = await DesktopPickers.PickFolderAsync(OwnerWindowHandle, "Choose folder");
                if (!IsCurrentDraft(generation) || selected is null) return;
                chosen.Add(PathRules.CreateSelectedExclusion(_settings, selected, true));
            }
            else
            {
                var selected = await DesktopPickers.PickFilesAsync(OwnerWindowHandle, "Choose files");
                if (!IsCurrentDraft(generation) || selected.Count == 0) return;
                foreach (var file in selected) chosen.Add(PathRules.CreateSelectedExclusion(_settings, file, false));
            }
            _selectionIsFolder = folder;
            ApplyPickedSelections(chosen);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentDraft(generation)) ShowPickerError(error, folder ? "folder" : "files");
        }
        finally { if (opened) EndPicker(generation, ChangeSelectionButton); }
    }

    private void ApplyPickedSelections(List<SelectedExclusion> chosen)
    {
        if (_editing?.Value is SelectedExclusion existing) chosen = chosen.Select(selection => selection with { Enabled = existing.Enabled }).ToList();
        _selections = chosen.Distinct().ToList();
        BuilderError.IsOpen = false;
        UpdatePreview();
    }

    private async void ExampleFile_Click(object sender, RoutedEventArgs args)
    {
        var generation = _builderGeneration;
        var opened = false;
        try
        {
            if (!TryBeginPicker(out generation)) return;
            opened = true;
            var file = await DesktopPickers.PickFileAsync(OwnerWindowHandle, "Use this file type");
            ApplyPickedExample(file, generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentDraft(generation)) ShowPickerError(error, "example file");
        }
        finally { if (opened) EndPicker(generation, ExampleFileButton); }
    }

    private void ApplyPickedExample(string? file, int generation)
    {
        if (!IsCurrentDraft(generation) || file is null) return;
        var extension = Path.GetExtension(file).TrimStart('.');
        if (extension.Length == 0)
        {
            ShowError("Choose a file with an extension, such as photo.jpg. To skip a file with no extension, use Choose files or a Name rule instead. Your current rule has been kept.",
                builder: true, title: "This file has no extension", severity: InfoBarSeverity.Warning);
            return;
        }
        ExtensionBox.Text = extension;
        BuilderError.IsOpen = false;
        UpdatePreview();
    }

    private async void ScopeFolder_Click(object sender, RoutedEventArgs args)
    {
        var generation = _builderGeneration;
        var opened = false;
        try
        {
            if (!TryBeginPicker(out generation)) return;
            opened = true;
            var folder = await DesktopPickers.PickFolderAsync(OwnerWindowHandle, "Apply to this folder");
            ApplyPickedScope(folder, generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentDraft(generation)) ShowPickerError(error, "folder");
        }
        finally { if (opened) EndPicker(generation, ScopeFolderButton); }
    }

    private void ApplyPickedScope(string? folder, int generation)
    {
        if (!IsCurrentDraft(generation) || folder is null) return;
        // ComboBox items belong to the retained draft, not necessarily the
        // latest backup configuration. Validate against current roots, and
        // selecting a root means its whole tree rather than an existing sub-scope.
        var root = _settings.CustomBackups.Select(backup => backup.SourcePath).Prepend(_settings.RootPath)
            .Select(NormalizeRoot).FirstOrDefault(path => PathEquals(path, folder));
        if (root is not null) SelectScope(new(ScopeDescription(root, null), root));
        else
        {
            var selection = PathRules.CreateSelectedExclusion(_settings, folder, true);
            SelectScope(new(ScopeDescription(selection.RootPath, selection.RelativePath), selection.RootPath, selection.RelativePath));
        }
        BuilderError.IsOpen = false;
        UpdatePreview();
    }

    private void ShowPickerError(Exception error, string selection)
    {
        // Native COM failures can have an empty Message. Never show an empty
        // red banner or echo an arbitrary filesystem path from an exception.
        var message = error switch
        {
            InvalidDataException => error.Message,
            InvalidOperationException => error.Message,
            UnauthorizedAccessException => "Windows could not access this selection. Choose a file or folder your Windows account can access.",
            IOException when error.InnerException is null && error.Message is
                "Linked files and folders cannot be selected for backup exclusions." or
                "Linked directories inside the sync folder cannot be synchronized." =>
                "This selection points to a linked file or folder. Choose its original location inside a configured backup instead.",
            _ => $"Windows could not open or use the {selection} chooser (0x{unchecked((uint)error.GetBaseException().HResult):X8}). Try again. Your current rule has been kept."
        };
        if (selection == "example file" && error is not InvalidDataException)
            message += " You can also enter the file extension above, such as jpg.";
        var label = selection switch { "files" => "files", "example file" => "an example file", _ => "a " + selection };
        ShowError(message, builder: true, title: "Could not choose " + label);
    }

    private async void Save_Click(object sender, RoutedEventArgs args) => await SaveDraftAsync();

    private async Task SaveDraftAsync()
    {
        if (_saving || _picking) return;
        _builderGeneration++;
        try
        {
            var candidate = CloneExclusions(_settings);
            if (_editing is not null) candidate = RemoveReference(candidate, _editing);
            if (_kind == EditorKind.Selection)
            {
                if (_selections.Count == 0) throw new InvalidDataException("Choose at least one file or folder.");
                foreach (var selected in _selections)
                {
                    var index = candidate.SelectedExclusions.FindIndex(existing => PathEquals(existing.RootPath, selected.RootPath) &&
                        existing.RelativePath.Equals(selected.RelativePath, StringComparison.OrdinalIgnoreCase) && existing.IsFolder == selected.IsFolder);
                    if (index >= 0) candidate.SelectedExclusions[index] = selected;
                    else candidate.SelectedExclusions.Add(selected);
                }
            }
            else
            {
                var rule = BuildGuidedRule();
                var index = candidate.GuidedExclusions.FindIndex(existing => (existing with { Enabled = true }) == (rule with { Enabled = true }));
                if (index >= 0) candidate.GuidedExclusions[index] = rule;
                else candidate.GuidedExclusions.Add(rule);
            }
            PathRules.ValidateSettings(candidate);
            if (await PersistAsync(candidate, builder: true)) CloseBuilder();
        }
        catch (Exception error) { ShowError(error.Message, builder: true); }
    }

    private async Task ChangeEnabledAsync(RuleReference reference, bool enabled)
    {
        try
        {
            var candidate = CloneExclusions(_settings);
            switch (reference.Value)
            {
                case SelectedExclusion selected:
                    var selectedIndex = candidate.SelectedExclusions.IndexOf(selected);
                    EnsureExists(selectedIndex);
                    candidate.SelectedExclusions[selectedIndex] = selected with { Enabled = enabled };
                    break;
                case GuidedExclusion guided:
                    var guidedIndex = candidate.GuidedExclusions.IndexOf(guided);
                    EnsureExists(guidedIndex);
                    candidate.GuidedExclusions[guidedIndex] = guided with { Enabled = enabled };
                    break;
                case string legacy:
                    EnsureExists(candidate.Exclusions.IndexOf(legacy));
                    candidate.DisabledLegacyExclusions.RemoveAll(value => value.Equals(legacy, StringComparison.OrdinalIgnoreCase));
                    if (!enabled) candidate.DisabledLegacyExclusions.Add(legacy);
                    break;
            }
            await PersistAsync(candidate, builder: false);
            FocusRule(reference);
        }
        catch (Exception error) { ShowError(error.Message, builder: false); RenderRows(); FocusRule(reference); }
    }

    private async Task RemoveAsync(RuleReference reference)
    {
        try
        {
            if (await PersistAsync(RemoveReference(CloneExclusions(_settings), reference), builder: false)) AddButton.Focus(FocusState.Programmatic);
            else FocusRule(reference);
        }
        catch (Exception error) { ShowError(error.Message, builder: false); }
    }

    private void FocusRule(RuleReference reference)
    {
        foreach (var row in _rowActions)
        {
            var same = (reference.Value, row.Reference.Value) switch
            {
                (SelectedExclusion first, SelectedExclusion second) => (first with { Enabled = true }) == (second with { Enabled = true }),
                (GuidedExclusion first, GuidedExclusion second) => (first with { Enabled = true }) == (second with { Enabled = true }),
                (string first, string second) => first == second, _ => false
            };
            if (same) { row.Toggle.Focus(FocusState.Programmatic); return; }
        }
        AddButton.Focus(FocusState.Programmatic);
    }

    private static AppSettings RemoveReference(AppSettings candidate, RuleReference reference)
    {
        switch (reference.Value)
        {
            case SelectedExclusion selected:
                EnsureExists(candidate.SelectedExclusions.IndexOf(selected));
                candidate.SelectedExclusions.Remove(selected);
                break;
            case GuidedExclusion guided:
                EnsureExists(candidate.GuidedExclusions.IndexOf(guided));
                candidate.GuidedExclusions.Remove(guided);
                break;
            case string legacy:
                EnsureExists(candidate.Exclusions.IndexOf(legacy));
                candidate.Exclusions.Remove(legacy);
                candidate.DisabledLegacyExclusions.RemoveAll(value => value.Equals(legacy, StringComparison.OrdinalIgnoreCase));
                break;
        }
        return candidate;
    }

    private static void EnsureExists(int index)
    {
        if (index < 0) throw new InvalidOperationException("This exclusion changed while you were editing. Cancel and reopen its latest version.");
    }

    private async Task<bool> PersistAsync(AppSettings candidate, bool builder)
    {
        if (_saving || _picking) return false;
        if (_presentationOnly) { ShowError("Changes are unavailable in this isolated preview.", builder); RenderRows(); return false; }
        if (SaveChangesAsync is null) { ShowError("Exclusions are not ready to save. Try again after the app finishes starting.", builder); RenderRows(); return false; }
        PathRules.ValidateSettings(candidate);
        var update = new PreferenceUpdate
        {
            Exclusions = candidate.Exclusions.ToArray(), DisabledLegacyExclusions = candidate.DisabledLegacyExclusions.ToArray(),
            SelectedExclusions = candidate.SelectedExclusions.ToArray(), GuidedExclusions = candidate.GuidedExclusions.ToArray()
        };
        SetBusy(true);
        try
        {
            await SaveChangesAsync(update);
            // Publish the accepted snapshot only after persistence succeeds; retain unrelated refreshed preferences.
            _settings = _settings with
            {
                Exclusions = [.. candidate.Exclusions], DisabledLegacyExclusions = [.. candidate.DisabledLegacyExclusions],
                SelectedExclusions = [.. candidate.SelectedExclusions], GuidedExclusions = [.. candidate.GuidedExclusions]
            };
            ErrorBar.IsOpen = false;
            BuilderError.IsOpen = false;
            return true;
        }
        catch (Exception error) { ShowError(error.Message, builder); return false; }
        finally { SetBusy(false); RenderRows(); }
    }

    private void SetBusy(bool saving)
    {
        _saving = saving;
        AddButton.IsEnabled = !saving && BuilderPanel.Visibility != Visibility.Visible;
        if (saving)
        {
            _disabledBuilderControls.Clear();
            CollectBuilderControlStates(BuilderPanel, _disabledBuilderControls);
            // Snapshot every effective state before disabling an Expander or other parent;
            // otherwise inherited false values would be mistaken for the original child state.
            foreach (var (control, _) in _disabledBuilderControls) control.IsEnabled = false;
        }
        else
        {
            foreach (var (control, wasEnabled) in _disabledBuilderControls) control.IsEnabled = wasEnabled;
            _disabledBuilderControls.Clear();
        }
        CancelButton.IsEnabled = !saving;
        BusyRing.IsActive = saving;
        BusyRing.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        RenderRows();
        UpdatePreview();
        UpdatePickerControls();
    }

    private static void CollectBuilderControlStates(DependencyObject parent, List<(Control Control, bool WasEnabled)> states)
    {
        if (parent is Control control)
        {
            states.Add((control, control.IsEnabled));
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            CollectBuilderControlStates(VisualTreeHelper.GetChild(parent, index), states);
    }

    private void ShowError(string message, bool builder, string title = "Exclusion was not changed", InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        var bar = builder ? BuilderError : ErrorBar;
        bar.Title = title;
        bar.Message = string.IsNullOrWhiteSpace(message) ? "The change could not be completed. Your existing exclusions and current rule have been kept. Try again." : message;
        bar.Severity = severity;
        bar.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) => CloseBuilder();
    private void CloseBuilder()
    {
        if (_saving) return;
        _builderGeneration++;
        BuilderPanel.Visibility = Visibility.Collapsed;
        _editing = null;
        _tokens.Clear();
        _selections = [];
        BuilderError.IsOpen = false;
        AddButton.IsEnabled = !_picking;
        RenderRows();
        AddButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Exercises the visual rule builder in memory; never invokes a provider, picker, or production save callback.</summary>
    public async Task RunUiValidationAsync(string outputDirectory, Func<string, Task>? capture = null)
    {
        var originalSettings = CloneExclusions(_settings);
        var originalPresentation = _presentationOnly;
        var originalSave = SaveChangesAsync;
        var originalWindowHandle = OwnerWindowHandle;
        var suffix = ActualTheme == ElementTheme.Light ? "light" : "dark";
        var log = Path.Combine(outputDirectory, "exclusions-validation.txt");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "CloudInlet-exclusion-presentation");
        var fixture = new AppSettings
        {
            RootPath = fixtureRoot, Exclusions = ["~$*", "*.bak"], DisabledLegacyExclusions = ["*.bak"],
            SelectedExclusions = [new(fixtureRoot, "Projects/design.psd", false), new(fixtureRoot, "Projects/cache", true)],
            GuidedExclusions = [new("*.tmp", ExclusionTarget.Files), new("cache", ExclusionTarget.Folders, fixtureRoot, "Projects", false)]
        };
        try
        {
            SetSettings(fixture, presentationOnly: true);
            await Capture("exclusion-list");
            Assert(RulesPanel.Children.Count == 6, "selected, guided, and legacy rows are individually visible");
            StartBuilder(EditorKind.Name);
            NameMatchBox.SelectedIndex = 1;
            NameBox.Text = "draft";
            SelectScope(new("Projects", fixtureRoot, "Projects"));
            var drafted = BuildGuidedRule();
            var stableRow = RulesPanel.Children[0];
            SetSettings(fixture with { UploadConcurrency = 7 }, presentationOnly: true);
            Assert(NameBox.Text == "draft" && BuildGuidedRule() == drafted, "background settings refresh preserves name and folder scope drafts");
            Assert(ReferenceEquals(stableRow, RulesPanel.Children[0]), "unchanged exclusion rows survive background transfer snapshots without losing focus or menus");
            StartBuilder(EditorKind.Type);
            Assert(_kind == EditorKind.Name && NameBox.Text == "draft" && !AddButton.IsEnabled, "starting another builder cannot silently replace an unsaved draft");
            TestPanel.IsExpanded = true;
            await AssertSampleMatch("my-draft.txt", "plain-language sample matches the generated rule");
            await Capture("exclusion-name-builder");
            CloseBuilder();
            StartBuilder(EditorKind.Advanced);
            _tokens.AddRange([new(TokenKind.Literal, "Projects"), new(TokenKind.Separator), new(TokenKind.Subfolders),
                new(TokenKind.Separator), new(TokenKind.Literal, "report-"), new(TokenKind.AnyCharacter), new(TokenKind.AnyText), new(TokenKind.Literal, ".tmp")]);
            RenderTokens();
            UpdatePreview();
            Assert(BuildGuidedRule().Pattern == "Projects/**/report-?*.tmp", "literal, directory, any-text, and single-character parts produce a complete path pattern");
            MoveToken(5, 1);
            Assert(BuildGuidedRule().Pattern == "Projects/**/report-*?.tmp", "matching parts can be reordered");
            MoveToken(6, -1);
            TestPanel.IsExpanded = true;
            await AssertSampleMatch("Projects/2026/report-a.tmp", "recursive directory pattern preview matches nested names");
            await AssertSampleMatch("Projects/report-a.tmp", "recursive directory part also matches zero subfolders");
            await Capture("exclusion-pattern-builder");
            _presentationOnly = false;
            SaveChangesAsync = _ => throw new IOException("Validation save failure. Your rule remains ready to retry.");
            var originalCount = _settings.GuidedExclusions.Count;
            await SaveDraftAsync();
            Assert(_settings.GuidedExclusions.Count == originalCount && BuilderPanel.Visibility == Visibility.Visible &&
                BuilderError.IsOpen && BuildGuidedRule().Pattern == "Projects/**/report-?*.tmp", "failed save preserves accepted rules and the complete retry draft");
            Assert(TestPathBox.IsEnabled && TestKindBox.IsEnabled && CancelButton.IsEnabled && LiteralBox.IsEnabled,
                "a failed save restores nested editor fields and cancellation for retry");
            await Capture("exclusion-save-failure");
            PreferenceUpdate? accepted = null;
            SaveChangesAsync = update => { accepted = update; return Task.CompletedTask; };
            await SaveDraftAsync();
            Assert(accepted?.GuidedExclusions?.Count == originalCount + 1 && _settings.GuidedExclusions.Count == originalCount + 1 &&
                BuilderPanel.Visibility == Visibility.Collapsed, "successful save publishes rules and closes the draft only after acceptance");
            await ChangeEnabledAsync(new(RuleKind.Legacy, "~$*"), false);
            Assert(_settings.Exclusions.SequenceEqual(fixture.Exclusions) && _settings.DisabledLegacyExclusions.Contains("~$*"), "disabling a legacy rule preserves its exact original pattern");
            await ChangeEnabledAsync(new(RuleKind.Legacy, "~$*"), true);
            Assert(!_settings.DisabledLegacyExclusions.Contains("~$*"), "legacy rules can be re-enabled without conversion");
            StartBuilder(EditorKind.Type);
            Assert(PreviewSummary.Text.Contains("Choose an example file", StringComparison.Ordinal) && !BuilderError.IsOpen,
                "a new file-type rule invites file selection without treating empty input as a failed action");
            ExtensionBox.Text = ".jpg";
            Assert(BuildGuidedRule() is { Pattern: "*.jpg", Target: ExclusionTarget.Files }, "file extension builder targets files rather than similarly named folders");
            ExtensionBox.Text = "tar.gz";
            Assert(BuildGuidedRule().Pattern == "*.tar.gz", "compound file endings can be entered through the file-type builder");
            SelectScope(new("Projects", fixtureRoot, "Projects"));
            var typeDraft = BuildGuidedRule();
            ApplyPickedExample(null, _builderGeneration);
            ApplyPickedScope(null, _builderGeneration);
            Assert(BuildGuidedRule() == typeDraft && !BuilderError.IsOpen,
                "cancelling an example-file or scope chooser preserves the entire draft without an error banner");
            var directoryScope = ScopeBox.SelectedItem as RuleScope ?? throw new InvalidOperationException("The fixture folder scope is missing.");
            ScopeBox.Items.Remove(directoryScope);
            ScopeBox.Items.Insert(0, directoryScope);
            ApplyPickedScope(fixtureRoot, _builderGeneration);
            Assert(BuildGuidedRule() is { RelativeDirectory: null } wholeRootRule && wholeRootRule.RootPath == fixtureRoot,
                "choosing a backup root applies to its whole tree even when a matching subfolder scope appears first");
            SelectScope(directoryScope);
            var pickerSettings = CloneExclusions(_settings);
            SetSettings(pickerSettings with { RootPath = fixtureRoot + "-changed", CustomBackups = [] }, _presentationOnly);
            var staleRootRejected = false;
            try { ApplyPickedScope(fixtureRoot, _builderGeneration); }
            catch (InvalidDataException) { staleRootRejected = true; }
            Assert(staleRootRejected && BuildGuidedRule() == typeDraft && ReferenceEquals(ScopeBox.SelectedItem, directoryScope),
                "a root removed while choosing a scope is rejected without silently replacing the retained draft");
            SetSettings(pickerSettings, _presentationOnly);
            ApplyPickedExample(Path.Combine(fixtureRoot, "README"), _builderGeneration);
            Assert(BuildGuidedRule() == typeDraft && BuilderError.IsOpen && BuilderError.Severity == InfoBarSeverity.Warning &&
                BuilderError.Message.Contains("Name rule", StringComparison.Ordinal),
                "an extensionless example explains name and selected-file alternatives while retaining the existing type and scope");
            ShowPickerError(new IOException("Could not open file chooser.",
                new System.Runtime.InteropServices.COMException("", unchecked((int)0x80004005))), "example file");
            Assert(BuildGuidedRule() == typeDraft && BuilderError.IsOpen && BuilderError.Severity == InfoBarSeverity.Error &&
                BuilderError.Message.Contains("0x80004005", StringComparison.Ordinal) && BuilderError.Message.Contains("file extension", StringComparison.Ordinal),
                "a native chooser failure with no exception message has actionable text and retains the draft");
            await Capture("exclusion-picker-failure");
            ShowPickerError(new IOException(Path.Combine(fixtureRoot, "private-name.txt")), "folder");
            Assert(!BuilderError.Message.Contains("private-name", StringComparison.Ordinal),
                "arbitrary native failure text does not expose a selected filesystem path");
            ShowError("  ", builder: true);
            Assert(!string.IsNullOrWhiteSpace(BuilderError.Message), "every exclusion error has a useful message even when its exception text is empty");
            BuilderError.IsOpen = false;
            var originalOwner = OwnerWindowHandle;
            OwnerWindowHandle = 0;
            ExampleFile_Click(ExampleFileButton, new RoutedEventArgs());
            Assert(!_picking && BuilderError.IsOpen && BuilderError.Message.Contains("window", StringComparison.OrdinalIgnoreCase) &&
                BuildGuidedRule() == typeDraft, "a chooser requested before window readiness gives useful feedback without changing the draft");
            OwnerWindowHandle = originalOwner;
            _presentationOnly = true;
            Assert(TryBeginPicker(out var pendingGeneration), "a read-only chooser can start in the isolated preview without permitting persistence");
            Assert(!TryBeginPicker(out _) && !SaveButton.IsEnabled && !ExampleFileButton.IsEnabled && !ScopeFolderButton.IsEnabled &&
                !ExtensionBox.IsEnabled && CancelButton.IsEnabled,
                "an outstanding chooser prevents duplicate selection and saving while retaining the ability to cancel the draft");
            await SaveDraftAsync();
            Assert(BuildGuidedRule() == typeDraft && _picking && pendingGeneration == _builderGeneration,
                "saving cannot race an outstanding chooser or invalidate its retained draft");
            CloseBuilder();
            ApplyPickedExample(Path.Combine(fixtureRoot, "later.png"), pendingGeneration);
            ApplyPickedScope(fixtureRoot, pendingGeneration);
            EndPicker(pendingGeneration, ExampleFileButton);
            Assert(BuilderPanel.Visibility == Visibility.Collapsed && ExtensionBox.Text == "tar.gz" && AddButton.IsEnabled && !BuilderError.IsOpen,
                "a chooser result arriving after draft cancellation cannot overwrite the next editor state");
            StartBuilder(EditorKind.Type);
            ApplyPickedExample(Path.Combine(fixtureRoot, "example.JPG"), _builderGeneration);
            Assert(BuildGuidedRule().Pattern == "*.JPG" && SaveButton.IsEnabled && !BuilderError.IsOpen,
                "an example file sets its extension directly and enables saving without opening or reading that file");
            CloseBuilder();
            _presentationOnly = false;
            const string legacyPath = "Projects/*/cache";
            SetSettings(_settings with { Exclusions = [.. _settings.Exclusions, legacyPath], DisabledLegacyExclusions = [.. _settings.DisabledLegacyExclusions, legacyPath] });
            OpenExisting(new(RuleKind.Legacy, legacyPath));
            Assert(BuilderHelp.Text.Contains("converts", StringComparison.Ordinal) && BuilderHelp.Text.Contains("differently", StringComparison.Ordinal), "editing a legacy path pattern explains its guided-matching conversion before saving");
            await SaveDraftAsync();
            Assert(_settings.Exclusions.SequenceEqual(fixture.Exclusions) && !_settings.DisabledLegacyExclusions.Contains(legacyPath) &&
                _settings.GuidedExclusions.Any(rule => rule.Pattern == legacyPath && !rule.Enabled), "legacy conversion removes only the edited pattern and preserves its disabled state");
            var disabledSelection = new SelectedExclusion(fixtureRoot, "Projects/old.txt", false, false);
            SetSettings(_settings with { SelectedExclusions = [.. _settings.SelectedExclusions, disabledSelection] });
            OpenExisting(new(RuleKind.Selected, disabledSelection));
            ApplyPickedSelections([PathRules.CreateSelectedExclusion(_settings, Path.Combine(fixtureRoot, "Projects", "new.txt"), false)]);
            Assert(_selections is [{ RelativePath: "Projects/new.txt", Enabled: false }], "changing a picked file retains its disabled state");
            await SaveDraftAsync();
            Assert(!_settings.SelectedExclusions.Contains(disabledSelection) && _settings.SelectedExclusions.Any(selection => selection.RelativePath == "Projects/new.txt" && !selection.Enabled), "saving a replacement selection updates only the edited literal exclusion");
            StartBuilder(EditorKind.Name);
            NameMatchBox.SelectedIndex = 1;
            NameBox.Text = " ";
            Assert(BuildGuidedRule().Pattern == "* *", "ordinary name text can match a space without writing a wildcard pattern");
            await File.AppendAllTextAsync(log, $"PASS {suffix}: no provider, picker, or production settings write occurred.{Environment.NewLine}");
        }
        finally
        {
            SaveChangesAsync = originalSave;
            OwnerWindowHandle = originalWindowHandle;
            _saving = false;
            if (_picking) EndPicker(_builderGeneration, ExampleFileButton);
            CloseBuilder();
            SetSettings(originalSettings, originalPresentation);
            SetBusy(false);
        }

        async Task Capture(string name)
        {
            UpdateLayout();
            await Task.Delay(60);
            if (capture is not null) await capture($"{name}-{suffix}");
            else await UiSmokeCapture.SaveAsync(this, Path.Combine(outputDirectory, $"{name}-{suffix}.png"));
        }
        async Task AssertSampleMatch(string path, string message)
        {
            TestPathBox.Text = path;
            // TextBox queues its native edit notification; sample results are read
            // after that notification has had a dispatcher turn to update the preview.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while ((_lastTestedPath != path || TestResult.Text != "This example would be excluded.") && wait.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(20);
            if (_lastTestedPath != path || TestResult.Text != "This example would be excluded.")
                throw new InvalidOperationException($"Exclusion UI validation failed: {message}. Actual preview: {TestResult.Text}");
            Assert(true, message);
        }
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Exclusion UI validation failed: " + message);
            File.AppendAllText(log, $"PASS {suffix}: {message}.{Environment.NewLine}");
        }
    }
}
