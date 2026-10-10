using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CloudInlet.Views;

/// <summary>Keeps actions in reading order and starts a new row only when they no longer fit.</summary>
public sealed class ActionWrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ActionWrapPanel),
        new PropertyMetadata(8d, static (owner, _) => ((ActionWrapPanel)owner).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set
        {
            if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(SpacingProperty, value);
        }
    }

    private double EffectiveSpacing => double.IsFinite(Spacing) ? Math.Max(0, Spacing) : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(0, availableSize.Width);
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            // Actions use their natural width. A long, wrapping label can use
            // the whole row, but must not demand more than the panel's width.
            child.Measure(new Size(width, double.PositiveInfinity));
        }

        var rows = BuildRows(width);
        return new Size(rows.Count == 0 ? 0 : rows.Max(row => row.Width),
            rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * EffectiveSpacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var row in BuildRows(Math.Max(0, finalSize.Width)))
        {
            var x = 0d;
            foreach (var item in row.Items)
            {
                item.Element.Arrange(new Rect(x, y + (row.Height - item.Height) / 2, item.Width, item.Height));
                x += item.Width + EffectiveSpacing;
            }
            y += row.Height + EffectiveSpacing;
        }
        return finalSize;
    }

    private List<Row> BuildRows(double availableWidth)
    {
        var rows = new List<Row>();
        var row = new Row();
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var width = Math.Min(availableWidth, child.DesiredSize.Width);
            var height = child.DesiredSize.Height;
            var gap = row.Items.Count == 0 ? 0 : EffectiveSpacing;
            if (row.Items.Count > 0 && row.Width + gap + width > availableWidth)
            {
                rows.Add(row);
                row = new Row();
                gap = 0;
            }
            row.Items.Add(new(child, width, height));
            row.Width += gap + width;
            row.Height = Math.Max(row.Height, height);
        }
        if (row.Items.Count > 0) rows.Add(row);
        return rows;
    }

    private sealed record Item(UIElement Element, double Width, double Height);
    private sealed class Row
    {
        internal List<Item> Items { get; } = [];
        internal double Width { get; set; }
        internal double Height { get; set; }
    }
}
