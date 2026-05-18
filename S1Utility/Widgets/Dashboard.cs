using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Controls;

namespace S1Utility.Widgets;

// Pure-static layout primitives shared by Tab 1 panels and Tab 2 inspector cards.
// All chrome (card frames, data rows, sub-headers, grids) and the standard
// knob container live here; nothing in this class holds patch/PRM state.
internal static class Dashboard
{
    // Accent-headed card: 6px dot + uppercase title in the accent colour, a 1px
    // accent→transparent gradient underline, then the body content.
    public static Border BuildPrmCard(string title, IBrush accent, Control body)
    {
        var accentClr = ((ISolidColorBrush)accent).Color;

        var dot = new Ellipse
        {
            Width             = 6,
            Height            = 6,
            Fill              = accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleText = new TextBlock
        {
            Text              = title,
            FontSize          = 8.5,
            FontWeight        = FontWeight.Bold,
            LetterSpacing     = 1.5,
            Foreground        = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0, 0, 0),
        };

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children    = { dot, titleText },
        };

        var headerUnderline = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 6),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x60, accentClr.R, accentClr.G, accentClr.B), 0),
                    new GradientStop(Color.FromArgb(0x00, accentClr.R, accentClr.G, accentClr.B), 1),
                },
            },
        };

        var content = new StackPanel
        {
            Children = { headerRow, headerUnderline, body },
        };

        return new Border
        {
            Background      = Palette.BgCard,
            BorderBrush     = Palette.BdCard,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(8, 6),
            Child           = content,
        };
    }

    // Single label · value row with a 1px bottom-border separator. Mutates the
    // passed-in valueLabel for font/colour/alignment so callers don't have to.
    public static Border BuildDataRow(string label, TextBlock valueLabel)
    {
        valueLabel.FontSize          = 9.5;
        valueLabel.Foreground        = Palette.FgVal;
        valueLabel.FontFamily        = Palette.MonoFont;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.TextAlignment     = TextAlignment.Right;

        var labelText = new TextBlock
        {
            Text              = label,
            FontSize          = 9.5,
            Foreground        = Palette.FgLabel,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(labelText, 0);   grid.Children.Add(labelText);
        Grid.SetColumn(valueLabel, 1);  grid.Children.Add(valueLabel);

        return new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush     = Palette.BdRow,
            Padding         = new Thickness(0, 2, 0, 2),
            Child           = grid,
        };
    }

    // Small accent sub-header used inside cards (e.g. CHORD inside VOICE).
    public static StackPanel BuildSubHeader(string title, IBrush accent) => new()
    {
        Margin = new Thickness(0, 4, 0, 4),
        Children =
        {
            new TextBlock
            {
                Text          = title,
                FontSize      = 8.5,
                FontWeight    = FontWeight.Bold,
                LetterSpacing = 1.2,
                Foreground    = accent,
            },
        },
    };

    // 3-column grid filled left-to-right with row-major wrap.
    public static Grid BuildThreeColGrid(IEnumerable<Control> items)
    {
        var list = items.ToList();
        int rows = (list.Count + 2) / 3;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", System.Math.Max(rows, 1)))),
            ColumnSpacing     = 10,
        };
        for (int i = 0; i < list.Count; i++)
        {
            Grid.SetRow(list[i], i / 3);
            Grid.SetColumn(list[i], i % 3);
            grid.Children.Add(list[i]);
        }
        return grid;
    }

    // 2-column grid. Row-major fill by default; columnMajor=true fills col 0
    // top-to-bottom before col 1 (use for tightly paired sequences like CHORD).
    public static Grid BuildTwoColGrid(IEnumerable<Control> items, bool columnMajor = false)
    {
        var list = items.ToList();
        int rows = (list.Count + 1) / 2;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", System.Math.Max(rows, 1)))),
            ColumnSpacing     = 10,
        };
        for (int i = 0; i < list.Count; i++)
        {
            int r, c;
            if (columnMajor) { r = i % rows; c = i / rows; }
            else             { r = i / 2;    c = i % 2; }
            Grid.SetRow(list[i], r);
            Grid.SetColumn(list[i], c);
            grid.Children.Add(list[i]);
        }
        return grid;
    }

    // Effects card sub-column: accent sub-header followed by stacked data rows.
    public static StackPanel BuildEffectsSubCol(string title, IBrush accent, IEnumerable<Control> rows)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(BuildSubHeader(title, accent));
        foreach (var r in rows) panel.Children.Add(r);
        return panel;
    }

    // Standard knob container: rotary + value label + name label. Tightens row
    // heights when containerWidth is small (used by the compact Voice row).
    public static Grid MakeKnobContainer(RotaryKnob knob, TextBlock valueLabel, TextBlock nameLabel, double containerWidth = 68)
    {
        bool small = containerWidth <= 58;
        var container = new Grid
        {
            Width          = containerWidth,
            Margin         = new Thickness(small ? 2 : 3, 4),
            RowDefinitions = small
                ? new RowDefinitions("56,14,20")
                : new RowDefinitions("56,14,26"),
        };
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(knob,       0);
        Grid.SetRow(valueLabel, 1);
        Grid.SetRow(nameLabel,  2);
        container.Children.Add(knob);
        container.Children.Add(valueLabel);
        container.Children.Add(nameLabel);
        return container;
    }
}
