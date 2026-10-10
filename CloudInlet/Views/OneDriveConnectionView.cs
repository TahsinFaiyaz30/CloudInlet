using CloudInlet.Application;
using CloudInlet.Core.OneDrive;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;

namespace CloudInlet.Views;

/// <summary>OneDrive account management, separate from choosing a transfer location.</summary>
internal sealed class OneDriveConnectionView : UserControl
{
    private readonly ClientController _controller;
    private readonly StackPanel _accounts = new() { Spacing = 8 };
    private readonly StackPanel _connectedSection = new() { Spacing = 12 };
    private readonly StackPanel _form = new() { Spacing = 16 };
    private readonly StackPanel _setup = new() { Spacing = 16 };
    private readonly StackPanel _verification = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly InfoBar _notice = new() { IsClosable = true, Visibility = Visibility.Collapsed };
    private readonly TextBox _clientId = new() { Header = "Microsoft application ID", MinHeight = 36 };
    private readonly TextBox _tenant = new()
    {
        Header = "Sign-in authority", MinHeight = 36,
        PlaceholderText = "common, consumers, or an organization tenant"
    };
    private readonly Button _connectAnother = SourceImportDialog.ActionButton("Connect another account");
    private readonly Button _signIn = SourceImportDialog.ActionButton("Sign in with Microsoft");
    private readonly Button _cancel = SourceImportDialog.ActionButton("Cancel");
    private readonly Button _verificationLink = SourceImportDialog.ActionButton("Open in browser");
    private readonly Button _copyLink = SourceImportDialog.ActionButton("Copy link");
    private readonly Button _copyCode = SourceImportDialog.ActionButton("Copy code");
    private readonly TextBlock _userCode = new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 28,
        IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _expires = SourceImportDialog.Text("", true);
    private readonly TextBlock _waitingStatus = SourceImportDialog.Text("Getting a sign-in code…", true);
    private readonly ProgressRing _progress = new()
    {
        Width = 20, Height = 20,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Grid _waiting = new() { ColumnSpacing = 10, Visibility = Visibility.Collapsed };
    private readonly Expander _advanced;
    private readonly Border _formSurface;
    private CancellationTokenSource? _signInOperation;
    private bool _subscribed;
    private bool _hasAccounts;
    private Uri? _verificationUri;
    private DateTimeOffset _codeExpiresUtc;

    public event EventHandler? ConnectionChanged;

    internal OneDriveConnectionView(ClientController controller, nint owner, bool showAdvanced = false)
    {
        _controller = controller;
        // Microsoft sign-in opens in the browser only when the user requests it.
        _ = owner;
        _clientId.Text = ReadOverride(OneDriveSignInConfiguration.ClientIdEnvironmentVariable,
            OneDriveSignInConfiguration.LegacyClientIdEnvironmentVariable, OneDriveSignInConfiguration.DefaultClientId);
        _tenant.Text = ReadOverride(OneDriveSignInConfiguration.TenantEnvironmentVariable,
            OneDriveSignInConfiguration.LegacyTenantEnvironmentVariable, OneDriveSignInConfiguration.DefaultTenant);

        var body = new StackPanel { Spacing = 20 };
        body.Children.Add(_notice);
        var heading = SourceImportDialog.Heading("Connected accounts");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        _connectedSection.Children.Add(heading);
        _connectedSection.Children.Add(_accounts);
        _connectedSection.Children.Add(_connectAnother);
        body.Children.Add(_connectedSection);

        var formHeading = SourceImportDialog.Heading("Connect an account");
        AutomationProperties.SetHeadingLevel(formHeading, AutomationHeadingLevel.Level2);
        _setup.Children.Add(formHeading);
        _setup.Children.Add(SourceImportDialog.Text("Use a personal, work, or school account. Microsoft will ask you to approve access to OneDrive in your browser.", true));
        var advancedFields = new StackPanel { Spacing = 16 };
        advancedFields.Children.Add(SourceImportDialog.Text("Change these only when using a custom Microsoft application or organization-specific sign-in.", true));
        advancedFields.Children.Add(SourceImportDialog.PairedContent(_clientId, _tenant, 300));
        var organization = SourceImportDialog.ActionButton("Use yxrcz organization");
        organization.Click += (_, _) => _tenant.Text = OneDriveSignInConfiguration.YxrczTenantId;
        advancedFields.Children.Add(organization);
        _advanced = new Expander
        {
            Header = "Advanced connection settings", Content = advancedFields,
            IsExpanded = showAdvanced, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _setup.Children.Add(_advanced);
        _form.Children.Add(_setup);

        var verificationHeading = SourceImportDialog.Heading("Finish signing in");
        AutomationProperties.SetHeadingLevel(verificationHeading, AutomationHeadingLevel.Level2);
        _verification.Children.Add(verificationHeading);
        _verification.Children.Add(SourceImportDialog.Text("Open in browser copies this code for you. Paste it on Microsoft’s sign-in page to continue.", true));
        var codeLabel = SourceImportDialog.Text("Sign-in code", true);
        var codeText = new StackPanel { Spacing = 4 };
        codeText.Children.Add(codeLabel); codeText.Children.Add(_userCode);
        AutomationProperties.SetLabeledBy(_userCode, codeLabel);
        AutomationProperties.SetName(_copyCode, "Copy sign-in code");
        var codeRow = new Grid { ColumnSpacing = 16 };
        codeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        codeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        codeRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        codeRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        codeRow.Children.Add(codeText); codeRow.Children.Add(_copyCode);
        _copyCode.VerticalAlignment = VerticalAlignment.Center;
        codeRow.SizeChanged += (_, _) =>
        {
            codeText.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _copyCode.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var stacked = codeRow.ActualWidth < codeText.DesiredSize.Width + _copyCode.DesiredSize.Width + 16;
            Grid.SetColumnSpan(codeText, stacked ? 2 : 1);
            Grid.SetColumn(_copyCode, stacked ? 0 : 1);
            Grid.SetRow(_copyCode, stacked ? 1 : 0);
            Grid.SetColumnSpan(_copyCode, stacked ? 2 : 1);
            codeRow.RowSpacing = stacked ? 12 : 0;
        };
        var codeSurface = new Border
        {
            Child = codeRow, Padding = new Thickness(16), CornerRadius = new CornerRadius(8),
            Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        };
        _verification.Children.Add(codeSurface);
        _verification.Children.Add(_expires);
        _form.Children.Add(_verification);

        _signIn.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        _verificationLink.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        _verificationLink.Visibility = Visibility.Collapsed;
        _copyLink.Visibility = Visibility.Collapsed;
        AutomationProperties.SetHelpText(_verificationLink, "Copies the sign-in code, then opens Microsoft sign-in in your browser.");
        ToolTipService.SetToolTip(_verificationLink, "Copy the sign-in code and open Microsoft sign-in");
        AutomationProperties.SetName(_copyLink, "Copy Microsoft sign-in link");
        ToolTipService.SetToolTip(_copyLink, "Copy the Microsoft sign-in page address");
        _actions.Children.Add(_signIn); _actions.Children.Add(_verificationLink); _actions.Children.Add(_copyLink); _actions.Children.Add(_cancel);
        _actions.SizeChanged += (_, _) => ReflowActions();
        _form.Children.Add(_actions);
        _waiting.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _waiting.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _waiting.Children.Add(_progress);
        Grid.SetColumn(_waitingStatus, 1); _waiting.Children.Add(_waitingStatus);
        AutomationProperties.SetLiveSetting(_waitingStatus, AutomationLiveSetting.Polite);
        _form.Children.Add(_waiting);
        _formSurface = SourceImportDialog.Surface(_form);
        body.Children.Add(_formSurface);
        Content = body;

        _connectAnother.Click += (_, _) => ShowForm();
        _notice.Closed += (_, _) => _notice.Visibility = Visibility.Collapsed;
        _signIn.Click += async (_, _) => await SignInAsync();
        _copyCode.Click += (_, _) => CopyCode();
        _copyLink.Click += (_, _) => CopyVerificationLink();
        _verificationLink.Click += async (_, _) => await OpenVerificationAsync();
        _cancel.Click += (_, _) =>
        {
            if (_signInOperation is { } operation) operation.Cancel();
            else
            {
                HideNotice();
                ClearVerification();
                if (_hasAccounts) HideForm();
            }
        };
        Loaded += (_, _) =>
        {
            if (!_subscribed) { _controller.Changed += ControllerChanged; _subscribed = true; }
            RefreshAccounts();
        };
        Unloaded += (_, _) =>
        {
            if (_subscribed) { _controller.Changed -= ControllerChanged; _subscribed = false; }
            _signInOperation?.Cancel();
            ClearVerification();
            _progress.IsActive = false;
        };
        FluentIconMotion.Attach(this);
        RefreshAccounts();
        if (showAdvanced || !_hasAccounts) ShowForm();
        else HideForm();
    }

    internal void ShowAdvancedSettings()
    {
        ShowForm();
        _advanced.IsExpanded = true;
    }

    internal void RefreshAccounts()
    {
        try
        {
            var accounts = _controller.OneDriveAccounts;
            _accounts.Children.Clear();
            foreach (var account in accounts)
            {
                _accounts.Children.Add(new SettingsCard
                {
                    Header = account.Name, Description = "Microsoft OneDrive",
                    HeaderIcon = FluentIcons.Create("person"),
                    Content = SourceImportDialog.Text("Connected", true)
                });
            }
            _hasAccounts = accounts.Count > 0;
            _connectedSection.Visibility = _hasAccounts ? Visibility.Visible : Visibility.Collapsed;
            _cancel.Visibility = _hasAccounts || _signInOperation is not null ? Visibility.Visible : Visibility.Collapsed;
            ReflowActions();
        }
        catch (Exception error) { ShowNotice(InfoBarSeverity.Error, "Couldn't load accounts", error.Message); }
    }

    private void ControllerChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsLoaded) RefreshAccounts();
    });

    private void ShowForm()
    {
        _formSurface.Visibility = Visibility.Visible;
        _connectAnother.Visibility = Visibility.Collapsed;
        _cancel.Visibility = _hasAccounts || _signInOperation is not null ? Visibility.Visible : Visibility.Collapsed;
        ReflowActions();
    }

    private void HideForm()
    {
        _formSurface.Visibility = Visibility.Collapsed;
        _connectAnother.Visibility = Visibility.Visible;
    }

    private async Task SignInAsync()
    {
        if (_signInOperation is not null || !IsLoaded) return;
        using var operation = new CancellationTokenSource();
        _signInOperation = operation;
        _signIn.IsEnabled = false;
        _setup.Visibility = _signIn.Visibility = Visibility.Collapsed;
        _cancel.Content = "Cancel sign-in";
        _cancel.Visibility = Visibility.Visible;
        _progress.IsActive = true;
        _waiting.Visibility = Visibility.Visible;
        _waitingStatus.Text = "Getting a sign-in code…";
        ClearVerification();
        ReflowActions();
        HideNotice();
        try
        {
            var options = OneDriveSignInConfiguration.Resolve(_clientId.Text, _tenant.Text);
            var code = await _controller.BeginOneDriveSignInAsync(options.ClientId, options.Tenant, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsLoaded) return;
            _userCode.Text = code.UserCode;
            _codeExpiresUtc = code.ExpiresUtc;
            _expires.Text = "Expires at " + code.ExpiresUtc.ToLocalTime().ToString("t") + ".";
            _verificationUri = code.VerificationUri;
            _copyCode.Content = "Copy code";
            _copyCode.IsEnabled = true;
            _copyLink.IsEnabled = true;
            _verification.Visibility = _verificationLink.Visibility = _copyLink.Visibility = Visibility.Visible;
            ReflowActions();
            _waitingStatus.Text = "Waiting for you to finish in your browser…";
            // Copy, browser launch, and cancellation stay usable throughout polling.
            var account = await _controller.CompleteOneDriveSignInAsync(options.ClientId, options.Tenant, code, operation.Token);
            if (!IsLoaded) return;
            RefreshAccounts();
            HideForm();
            ShowNotice(InfoBarSeverity.Success, "OneDrive connected", account.Name + " is ready for cloud transfers.");
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            HideNotice();
        }
        catch (Exception error)
        {
            if (!operation.IsCancellationRequested && IsLoaded)
                ShowNotice(InfoBarSeverity.Error, "Couldn't connect OneDrive", error.Message);
        }
        finally
        {
            _signInOperation = null;
            _signIn.IsEnabled = true;
            _setup.Visibility = _signIn.Visibility = Visibility.Visible;
            _cancel.Content = "Cancel";
            _cancel.Visibility = _hasAccounts ? Visibility.Visible : Visibility.Collapsed;
            _progress.IsActive = false;
            _waiting.Visibility = Visibility.Collapsed;
            ClearVerification();
            ReflowActions();
        }
    }

    private void ReflowActions()
    {
        var visible = _actions.Children.OfType<FrameworkElement>().Where(item => item.Visibility == Visibility.Visible).ToArray();
        foreach (var item in visible) item.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var needed = visible.Sum(item => item.DesiredSize.Width) + Math.Max(0, visible.Length - 1) * _actions.Spacing;
        _actions.Orientation = _actions.ActualWidth < needed ? Orientation.Vertical : Orientation.Horizontal;
    }

    private bool HasActiveCode => IsLoaded && _signInOperation is { IsCancellationRequested: false } &&
        _userCode.Text.Length > 0 && DateTimeOffset.UtcNow < _codeExpiresUtc;

    private bool CopyCode()
    {
        if (!HasActiveCode) return false;
        try
        {
            var data = new DataPackage();
            data.SetText(_userCode.Text);
            if (!Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
                throw new InvalidOperationException("The clipboard is busy. Try again, or select the code and copy it.");
            _copyCode.Content = "Copied";
            _waitingStatus.Text = "Code copied. Finish signing in in your browser.";
            return true;
        }
        catch (Exception error)
        {
            ShowNotice(InfoBarSeverity.Error, "Couldn't copy the code", error.Message);
            return false;
        }
    }

    private async Task OpenVerificationAsync()
    {
        if (!HasActiveCode || _verificationUri is not { } uri) return;
        var operation = _signInOperation;
        if (!CopyCode()) return;
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(uri))
                throw new InvalidOperationException("Windows couldn't open your browser. Try opening the Microsoft sign-in page again.");
        }
        catch (Exception error)
        {
            if (IsLoaded && ReferenceEquals(operation, _signInOperation) && operation is { IsCancellationRequested: false })
                ShowNotice(InfoBarSeverity.Error, "Couldn't open Microsoft sign-in", error.Message);
        }
    }

    private void CopyVerificationLink()
    {
        if (!HasActiveCode || _verificationUri is not { } uri) return;
        try
        {
            var data = new DataPackage();
            data.SetText(uri.AbsoluteUri);
            if (!Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
                throw new InvalidOperationException("The clipboard is busy. Try copying the link again.");
            _copyLink.Content = "Link copied";
            _waitingStatus.Text = "Link copied. Open it in your browser and enter the sign-in code.";
            ReflowActions();
        }
        catch (Exception error) { ShowNotice(InfoBarSeverity.Error, "Couldn't copy the link", error.Message); }
    }

    private void ClearVerification()
    {
        _verification.Visibility = _verificationLink.Visibility = _copyLink.Visibility = Visibility.Collapsed;
        _verificationUri = null;
        _userCode.Text = "";
        _expires.Text = "";
        _codeExpiresUtc = default;
        _copyCode.IsEnabled = false;
        _copyCode.Content = "Copy code";
        _copyLink.IsEnabled = false;
        _copyLink.Content = "Copy link";
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        _notice.Severity = severity;
        _notice.Title = title;
        _notice.Message = message;
        _notice.Visibility = Visibility.Visible;
        _notice.IsOpen = true;
    }

    private void HideNotice()
    {
        _notice.IsOpen = false;
        _notice.Visibility = Visibility.Collapsed;
    }

    private static string ReadOverride(string current, string legacy, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(current);
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(legacy);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
