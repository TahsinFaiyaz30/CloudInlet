using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;
using SettingsExpander = CommunityToolkit.WinUI.Controls.SettingsExpander;

namespace CloudInlet.Views;

/// <summary>Shared, interaction-driven motion for native Fluent icons.</summary>
internal static class FluentIconMotion
{
    private static readonly ConditionalWeakTable<UIElement, Interaction> Interactions = new();
    private static readonly ConditionalWeakTable<MenuFlyout, object> Flyouts = new();

    internal static void Attach(UIElement root) => Interactions.GetValue(root, element => new Interaction(element));

    internal static void Attach(MenuFlyout menu) => Flyouts.GetValue(menu, flyout =>
    {
        // Flyouts are separate popup trees. Attach their controls after opening
        // and refresh even previously hidden submenu icons for the current theme.
        flyout.Opened += (_, _) => AttachMenuItems(flyout.Items, new AccessibilitySettings().HighContrast);
        return new object();
    });

    private static void AttachMenuItems(IEnumerable<MenuFlyoutItemBase> items, bool highContrast)
    {
        foreach (var item in items)
        {
            Attach(item);
            if (MenuIcon(item) is { } icon) FluentIcons.RefreshContrast(icon, highContrast);
            if (item is MenuFlyoutSubItem subMenu) AttachMenuItems(subMenu.Items, highContrast);
        }
    }

    private static IconElement? MenuIcon(MenuFlyoutItemBase item) => item switch
    {
        ToggleMenuFlyoutItem toggle => toggle.Icon,
        MenuFlyoutItem command => command.Icon,
        MenuFlyoutSubItem subMenu => subMenu.Icon,
        _ => null
    };

    private sealed class Interaction
    {
        private readonly UIElement _root;
        private readonly UISettings _settings = new();
        private readonly AccessibilitySettings _accessibility = new();
        private IconElement? _active;
        private string _activeState = "Normal";
        private bool _subscribed;
        private readonly PointerEventHandler _move, _press, _release, _exit;

        internal Interaction(UIElement root)
        {
            _root = root;
            _move = (_, args) => SetActive(FindIcon(args.OriginalSource as DependencyObject), "PointerOver");
            _press = (_, args) => SetActive(FindIcon(args.OriginalSource as DependencyObject), "Pressed", true);
            _release = (_, args) => SetActive(FindIcon(args.OriginalSource as DependencyObject), "PointerOver", true);
            _exit = (_, _) => SetActive(null, "Normal");
            root.AddHandler(UIElement.PointerMovedEvent, _move, true);
            root.AddHandler(UIElement.PointerPressedEvent, _press, true);
            root.AddHandler(UIElement.PointerReleasedEvent, _release, true);
            root.PointerExited += _exit;
            root.PointerCanceled += _exit;
            root.PointerCaptureLost += _exit;
            root.GotFocus += (_, args) => SetActive(FindIcon(args.OriginalSource as DependencyObject), "PointerOver");
            root.LostFocus += (_, _) => SetActive(null, "Normal");
            if (root is FrameworkElement element)
            {
                element.Loaded += (_, _) => Subscribe();
                element.Unloaded += (_, _) =>
                {
                    SetActive(null, "Normal");
                    _settings.AnimationsEnabledChanged -= AnimationsChanged;
                    _settings.ColorValuesChanged -= ContrastChanged;
                    _subscribed = false;
                };
                if (element.IsLoaded) Subscribe();
            }
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            _settings.AnimationsEnabledChanged += AnimationsChanged;
            // AccessibilitySettings.HighContrastChanged is not supported in
            // every unpackaged desktop context. UISettings tracks the same
            // system color changes without requiring a UWP window identity.
            _settings.ColorValuesChanged += ContrastChanged;
            FluentIcons.RefreshContrast(_root, _accessibility.HighContrast);
        }

        private void AnimationsChanged(UISettings sender, object args) => _root.DispatcherQueue.TryEnqueue(() =>
        {
            // Drop the previous gesture so entering the same icon after the
            // preference changes can start a fresh transition.
            SetActive(null, "Normal");
        });

        private void ContrastChanged(UISettings sender, object args) => _root.DispatcherQueue.TryEnqueue(() =>
            FluentIcons.RefreshContrast(_root, _accessibility.HighContrast));

        private void SetActive(IconElement? icon, string state, bool force = false)
        {
            if (ReferenceEquals(icon, _active) && state == _activeState && !force) return;
            if (_active is not null && !ReferenceEquals(icon, _active)) Animate(_active, "Normal");
            _active = icon;
            _activeState = state;
            if (icon is not null) Animate(icon, state);
        }

        private IconElement? FindIcon(DependencyObject? source)
        {
            IconElement? direct = null;
            for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is Control { IsEnabled: false }) return null;
                if (current is IconElement icon) direct = icon;
                if (current is NavigationViewItem navigation) return navigation.Icon;
                if (current is AutoSuggestBox search) return search.QueryIcon;
                if (current is SettingsCard card) return card.HeaderIcon as IconElement ?? direct;
                if (current is SettingsExpander expander) return expander.HeaderIcon as IconElement ?? direct;
                if (current is MenuFlyoutItemBase menu) return MenuIcon(menu);
                if (current is ButtonBase button)
                {
                    // An expander's internal toggle owns its header. Keep the
                    // native chevron animation and animate the leading icon.
                    for (var parent = VisualTreeHelper.GetParent(button); parent is not null && !ReferenceEquals(parent, _root); parent = VisualTreeHelper.GetParent(parent))
                    {
                        if (parent is SettingsExpander owner) return owner.HeaderIcon as IconElement;
                        if (parent is SettingsCard ownerCard) return ownerCard.HeaderIcon as IconElement;
                        if (parent is ButtonBase or ScrollViewer) break;
                    }
                    return direct ?? FirstIcon(button);
                }
                if (ReferenceEquals(current, _root)) break;
            }
            return direct;
        }

        private static IconElement? FirstIcon(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is IconElement icon) return icon;
                if (FirstIcon(child) is { } nested) return nested;
            }
            return null;
        }

        private void Animate(IconElement icon, string state)
        {
            if (icon is AnimatedIcon native)
            {
                for (var parent = VisualTreeHelper.GetParent(native); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is NavigationViewItem) return;
                // WinUI applies the appropriate static fallback when Windows
                // animations are disabled; do not add a second transform.
                AnimatedIcon.SetState(native, _settings.AnimationsEnabled ? state : "Normal");
                return;
            }

            // Structural affordances keep their native behavior. Only icons
            // with a semantic role participate in the shared gesture system.
            var kind = FluentIcons.KindOf(icon);
            if (kind is null) return;
            var visual = ElementCompositionPreview.GetElementVisual(icon);
            ElementCompositionPreview.SetIsTranslationEnabled(icon, true);
            visual.CenterPoint = new Vector3((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0);
            visual.StopAnimation("Scale");
            visual.StopAnimation("RotationAngleInDegrees");
            visual.StopAnimation("Translation");
            if (!_settings.AnimationsEnabled)
            {
                visual.Scale = Vector3.One;
                visual.RotationAngleInDegrees = 0;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                return;
            }
            var over = state == "PointerOver";
            var pressed = state == "Pressed";
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
            var scale = compositor.CreateVector3KeyFrameAnimation();
            var turn = compositor.CreateScalarKeyFrameAnimation();
            var travel = compositor.CreateVector3KeyFrameAnimation();
            scale.Duration = turn.Duration = travel.Duration = TimeSpan.FromMilliseconds(pressed ? 90 : over ? 620 : 160);
            if (over)
            {
                scale.InsertKeyFrame(1, Vector3.One, ease);
                // Each family has a brief recognizable gesture, then settles.
                // Sync is a single turn, never an invented busy indication.
                switch (kind)
                {
                    case "alert":
                        turn.InsertKeyFrame(0.16f, -20, ease);
                        turn.InsertKeyFrame(0.38f, 17, ease);
                        turn.InsertKeyFrame(0.6f, -11, ease);
                        turn.InsertKeyFrame(0.8f, 6, ease);
                        break;
                    case "settings":
                        turn.InsertKeyFrame(0.68f, 66, ease);
                        turn.InsertKeyFrame(0.85f, 57, ease);
                        turn.InsertKeyFrame(1, 60, ease);
                        break;
                    case "sync":
                        turn.InsertKeyFrame(1, 360, ease);
                        break;
                    case "folder":
                    case "document":
                    case "edit":
                        turn.InsertKeyFrame(0.3f, -10, ease);
                        turn.InsertKeyFrame(0.68f, 4, ease);
                        travel.InsertKeyFrame(0.3f, new Vector3(0, -2, 0), ease);
                        break;
                    case "upload":
                    case "download":
                        var direction = kind == "upload" ? -1 : 1;
                        travel.InsertKeyFrame(0.3f, new Vector3(0, direction * 5, 0), ease);
                        travel.InsertKeyFrame(0.7f, new Vector3(0, direction * -2, 0), ease);
                        break;
                    case "cloud":
                    case "home":
                    case "power":
                        travel.InsertKeyFrame(0.32f, new Vector3(0, -4, 0), ease);
                        travel.InsertKeyFrame(0.72f, new Vector3(0, 1, 0), ease);
                        break;
                    case "music":
                    case "video":
                    case "image":
                    case "paint-brush":
                        turn.InsertKeyFrame(0.27f, 12, ease);
                        turn.InsertKeyFrame(0.62f, -6, ease);
                        break;
                    case "search":
                    case "history":
                    case "clock":
                        turn.InsertKeyFrame(0.4f, -24, ease);
                        turn.InsertKeyFrame(0.75f, 7, ease);
                        break;
                    default:
                        travel.InsertKeyFrame(0.32f, new Vector3(0, -2, 0), ease);
                        break;
                }
                if (kind is not "settings" and not "sync") turn.InsertKeyFrame(1, 0, ease);
            }
            else
            {
                var targetScale = pressed ? 0.96f : 1;
                scale.InsertKeyFrame(1, new Vector3(targetScale, targetScale, 1), ease);
                turn.InsertKeyFrame(1, 0, ease);
            }
            travel.InsertKeyFrame(1, Vector3.Zero, ease);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("RotationAngleInDegrees", turn);
            visual.StartAnimation("Translation", travel);
        }
    }
}
