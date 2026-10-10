using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.AnimatedVisuals;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CloudInlet.Views;

/// <summary>One asset vocabulary for navigation, settings, and commands.</summary>
internal static class FluentIcons
{
    private sealed record Definition(string Kind, string Glyph, double Size);
    private static readonly ConditionalWeakTable<IconSource, Definition> Definitions = new();
    private static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>
    {
        ["home"] = "\uE80F", ["history"] = "\uE81C", ["clock"] = "\uE823",
        ["document"] = "\uE8A5", ["cloud"] = "\uE753", ["settings"] = "\uE713",
        ["sync"] = "\uE895", ["alert"] = "\uE7E7", ["paint-brush"] = "\uE790",
        ["person"] = "\uE77B", ["add"] = "\uE710", ["edit"] = "\uE70F",
        ["video"] = "\uE714", ["image"] = "\uEB9F", ["warning"] = "\uE7BA",
        ["checkmark"] = "\uE73E", ["globe"] = "\uE774", ["lock"] = "\uE72E",
        ["link"] = "\uE71B", ["apps"] = "\uE9E9", ["shield"] = "\uEA18",
        ["database"] = "\uE968", ["folder"] = "\uE8B7", ["storage"] = "\uEDA2",
        ["download"] = "\uE896", ["upload"] = "\uE898", ["power"] = "\uE7E8",
        ["battery"] = "\uE945", ["filter"] = "\uE8D2", ["info"] = "\uE946",
        ["search"] = "\uE721", ["delete"] = "\uE74D", ["music"] = "\uE767",
        ["desktop"] = "\uE7F8"
    };

    internal static IconElement Create(string kind, double size = 24)
    {
        // These are the same animated visuals used by WinUI's navigation and
        // search controls, including their hover/pressed/selected transitions.
        if (kind is "settings" or "search")
        {
            var icon = new AnimatedIcon
            {
                Source = kind == "settings" ? new AnimatedSettingsVisualSource() : new AnimatedFindVisualSource(),
                FallbackIconSource = new FontIconSource { Glyph = Glyphs[kind], FontSize = size },
                Width = size, Height = size, Tag = kind
            };
            AnimatedIcon.SetState(icon, "Normal");
            return icon;
        }
        return new IconSourceElement
        {
            IconSource = CreateSource(kind, size), Width = size, Height = size, Tag = kind
        };
    }

    internal static IconSource Source(string kind, double size = 24) => CreateSource(kind, size);

    internal static IconElement FromGlyph(string glyph, double size = 24)
    {
        var kind = KindFromGlyph(glyph);
        return kind is null
            ? new FontIcon { Glyph = glyph, FontSize = size, Width = size, Height = size }
            : Create(kind, size);
    }

    internal static IconSource SourceFromGlyph(string glyph, double size = 24) => KindFromGlyph(glyph) is { } kind
        ? CreateSource(kind, size) : new FontIconSource { Glyph = glyph, FontSize = size };

    internal static string? KindFromGlyph(string glyph) => glyph switch
    {
        "\uE8AB" or "\uE839" => "sync",
        "\uE701" => "globe",
        "\uE770" => "power",
        "\uE7F4" => "alert",
        "\uE787" => "clock",
        "\uE71C" => "filter",
        _ => Glyphs.FirstOrDefault(pair => pair.Value == glyph).Key
    };

    internal static IconSource CreateSource(string kind, double size = 24)
    {
        // Unknown names retain a legible icon instead of a missing asset.
        if (!Glyphs.TryGetValue(kind, out var glyph)) { kind = "document"; glyph = Glyphs[kind]; }
        return CreateSource(new Definition(kind, glyph, size), new AccessibilitySettings().HighContrast);
    }

    private static IconSource CreateSource(Definition definition, bool highContrast)
    {
        IconSource source;
        if (highContrast)
            source = new FontIconSource { Glyph = definition.Glyph, FontSize = definition.Size };
        else if (!WindowsSystemIcons.TryCreateSource(definition.Kind, definition.Size, out source))
            source = new BitmapIconSource
            {
                UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Fluent", definition.Kind + ".png")),
                ShowAsMonochrome = false
            };
        Definitions.Add(source, definition);
        return source;
    }

    internal static string? KindOf(IconElement icon) => icon switch
    {
        { Tag: string kind } when Glyphs.ContainsKey(kind) => kind,
        IconSourceElement { IconSource: BitmapIconSource { UriSource: { } uri } } => Path.GetFileNameWithoutExtension(uri.LocalPath),
        IconSourceElement { IconSource: FontIconSource font } => KindFromGlyph(font.Glyph),
        IconSourceElement { IconSource: { } source } when Definitions.TryGetValue(source, out var definition) => definition.Kind,
        FontIcon font => KindFromGlyph(font.Glyph),
        _ => null
    };

    internal static void RefreshContrast(DependencyObject root, bool highContrast)
    {
        if (root is IconSourceElement { IconSource: { } source } element && DefinitionOf(element, source) is { } definition
            && highContrast != (source is FontIconSource))
        {
            // Re-evaluate a bound glyph through its converter without replacing
            // the binding: activity and status icons must keep following state.
            var binding = element.GetBindingExpression(IconSourceElement.IconSourceProperty)?.ParentBinding;
            if (binding is not null)
            {
                element.ClearValue(IconSourceElement.IconSourceProperty);
                element.SetBinding(IconSourceElement.IconSourceProperty, binding);
            }
            else element.IconSource = CreateSource(definition, highContrast);
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            RefreshContrast(VisualTreeHelper.GetChild(root, index), highContrast);
    }

    private static Definition? DefinitionOf(IconSourceElement element, IconSource source)
    {
        if (Definitions.TryGetValue(source, out var definition)) return definition;
        // WinRT can return another managed wrapper for the same IconSource.
        // Recover our semantic metadata without depending on wrapper identity.
        if (KindOf(element) is not { } kind || !Glyphs.TryGetValue(kind, out var glyph)) return null;
        var size = source is FontIconSource font ? font.FontSize : element.Width;
        return new Definition(kind, glyph, double.IsFinite(size) && size > 0 ? size : 24);
    }
}

[MarkupExtensionReturnType(ReturnType = typeof(IconSource))]
public sealed class FluentIconExtension : MarkupExtension
{
    public string Kind { get; set; } = "document";
    public double Size { get; set; } = 24;
    protected override object ProvideValue() => FluentIcons.CreateSource(Kind, Size);
}

public sealed class FluentGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var size = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && parsed > 0 ? parsed : 20;
        return FluentIcons.SourceFromGlyph(value as string ?? "\uE8A5", size);
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
}
