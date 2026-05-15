using System;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;

namespace S1Utility.Widgets;

// Tab 2 PRM-only visualizers: the 32-bar DRAW waveform and the 16×4 CHOP grid.
// Pure statics that take the data source (PrmFileManager) and the accent brush.
internal static class PrmVisualizers
{
    // Tab 2 chop-grid "off" cell colour. Distinct from BgApp by a single channel
    // and unique to this widget, so it lives here as a private static.
    private static readonly IBrush ChopLedOff = new SolidColorBrush(Color.Parse("#1A1A20"));

    // Centerline colour for the DRAW bars dashed midline. Unique to this widget.
    private static readonly IBrush CenterlineStroke = new SolidColorBrush(Color.Parse("#2A2A33"));

    // 32 signed-byte bars (16 PRM points × 2 bytes each) with a dashed centerline
    // and signed numeric labels underneath.
    public static Panel MakeDrawBarsControl(PrmFileManager prm, IBrush accent)
    {
        const double BarH = 140.0;
        const double Half = BarH / 2.0;

        var posBars   = new Border[16];
        var negBars   = new Border[16];
        var valLabels = new TextBlock[16];

        void UpdateBars()
        {
            for (int pt = 0; pt < DrawWave.Points; pt++)
            {
                int signed = prm.DrawWave.GetPoint(pt);
                int raw    = signed < 0 ? signed + 65536 : signed;
                int lo     = raw & 0xFF;
                int hi     = (raw >> 8) & 0xFF;
                int[] pads = { lo > 127 ? lo - 256 : lo, hi > 127 ? hi - 256 : hi };

                for (int b = 0; b < 2; b++)
                {
                    int    idx  = pt * 2 + b;
                    int    v    = pads[b];
                    double norm = Math.Clamp(v / 100.0, -1.0, 1.0);
                    double mag  = Math.Abs(norm);
                    double h    = Math.Max(1.0, mag * (Half - 2));

                    posBars[idx].Height  = v > 0 ? h : 0;
                    posBars[idx].Opacity = 0.5 + mag * 0.5;
                    negBars[idx].Height  = v < 0 ? h : 0;
                    negBars[idx].Opacity = 0.5 + mag * 0.5;
                    valLabels[idx].Text  = v > 0 ? $"+{v}" : v.ToString();
                }
            }
        }

        var barsCanvas = new Grid
        {
            Height            = BarH,
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", 16))),
        };

        var centerline = new Rectangle
        {
            Height              = 1,
            Fill                = Brushes.Transparent,
            Stroke              = CenterlineStroke,
            StrokeThickness     = 1,
            StrokeDashArray     = new AvaloniaList<double> { 2, 2 },
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumnSpan(centerline, 16);
        barsCanvas.Children.Add(centerline);

        for (int i = 0; i < 16; i++)
        {
            var colGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,*"),
                Margin         = new Thickness(1, 0),
            };

            var posBar = new Border
            {
                Background          = accent,
                Width               = double.NaN,
                Height              = 0,
                VerticalAlignment   = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius        = new CornerRadius(1, 1, 0, 0),
                Margin              = new Thickness(0, 0, 0, 1),
            };
            var negBar = new Border
            {
                Background          = accent,
                Width               = double.NaN,
                Height              = 0,
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius        = new CornerRadius(0, 0, 1, 1),
                Margin              = new Thickness(0, 1, 0, 0),
            };
            posBars[i] = posBar;
            negBars[i] = negBar;

            Grid.SetRow(posBar, 0); colGrid.Children.Add(posBar);
            Grid.SetRow(negBar, 1); colGrid.Children.Add(negBar);

            Grid.SetColumn(colGrid, i);
            barsCanvas.Children.Add(colGrid);
        }

        var barsBorder = new Border
        {
            Background   = Palette.BgApp,
            CornerRadius = new CornerRadius(2),
            Padding      = new Thickness(2, 0),
            Child        = barsCanvas,
        };

        var labelsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", 16))),
            Margin            = new Thickness(2, 2, 2, 0),
        };
        for (int i = 0; i < 16; i++)
        {
            var lbl = new TextBlock
            {
                FontSize            = 7.5,
                FontFamily          = Palette.MonoFont,
                Foreground          = Palette.FgLabel,
                Text                = "0",
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            valLabels[i] = lbl;
            Grid.SetColumn(lbl, i);
            labelsGrid.Children.Add(lbl);
        }

        UpdateBars();
        prm.DrawWave.PointsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateBars);

        return new StackPanel { Children = { barsBorder, labelsGrid } };
    }

    // 16×4 LED matrix with waveform row labels (Square / Saw / Sub / Noise).
    // Cells are small rounded rectangles — circles+glow looked fuzzy at this size.
    public static Control MakeChopPatternControl(PrmFileManager prm, IBrush accent)
    {
        var labelColumn = new Grid
        {
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", ChopPattern.Waveforms))),
            Margin         = new Thickness(0, 0, 6, 0),
        };
        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            var lbl = new TextBlock
            {
                Text                = ChopPattern.WaveformNames[w],
                FontSize            = 9.5,
                Foreground          = Palette.FgLabel,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(lbl, w);
            labelColumn.Children.Add(lbl);
        }

        var ledGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", ChopPattern.Steps))),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", ChopPattern.Waveforms))),
            ColumnSpacing     = 3,
            RowSpacing        = 3,
            Height            = 140,
        };

        var cells = new Border[ChopPattern.Waveforms, ChopPattern.Steps];
        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            for (int s = 0; s < ChopPattern.Steps; s++)
            {
                bool on = prm.ChopPattern.GetStep(w, s);
                var cell = new Border
                {
                    Background          = on ? accent : ChopLedOff,
                    CornerRadius        = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    MinWidth            = 8,
                };
                cells[w, s] = cell;
                Grid.SetRow(cell, w);
                Grid.SetColumn(cell, s);
                ledGrid.Children.Add(cell);
            }
        }

        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            int waveform = w;
            prm.ChopPattern.PatternChanged += (_, changedWaveform) =>
            {
                if (changedWaveform != waveform) return;
                Dispatcher.UIThread.Post(() =>
                {
                    for (int s = 0; s < ChopPattern.Steps; s++)
                    {
                        bool on = prm.ChopPattern.GetStep(waveform, s);
                        cells[waveform, s].Background = on ? accent : ChopLedOff;
                    }
                });
            };
        }

        var ledArea = new Border
        {
            Background   = Palette.BgApp,
            CornerRadius = new CornerRadius(2),
            Padding      = new Thickness(3),
            Child        = ledGrid,
        };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(labelColumn, 0); layout.Children.Add(labelColumn);
        Grid.SetColumn(ledArea,     1); layout.Children.Add(ledArea);
        return layout;
    }
}
