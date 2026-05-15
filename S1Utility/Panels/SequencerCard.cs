using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// SEQUENCER inspector card — PATTERN / ARPEGGIATOR / AUTOMATION / D-MOTION
// sub-columns with a "VIEW STEPS →" header button that opens the dedicated
// sequencer window. The tempo and motion-lane labels are owned by MainWindow
// (it sets their Text on PrmMetaLoaded); they're passed in so this card just
// places them.
internal sealed class SequencerCard
{
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;
    private readonly IBrush _dmAccent;
    private readonly TextBlock _tempoLabel;
    private readonly TextBlock[] _motionCcLabels;
    private readonly Action _openSequencer;

    public SequencerCard(
        PrmFileManager prm,
        IBrush accent,
        IBrush dmAccent,
        TextBlock tempoLabel,
        TextBlock[] motionCcLabels,
        Action openSequencer)
    {
        _prm            = prm;
        _accent         = accent;
        _dmAccent       = dmAccent;
        _tempoLabel     = tempoLabel;
        _motionCcLabels = motionCcLabels;
        _openSequencer  = openSequencer;
    }

    public Border Build()
    {
        var accentClr = ((ISolidColorBrush)_accent).Color;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto,*"),  // pattern, arp, motion, divider, d-motion
            ColumnSpacing     = 14,
        };

        var patCol = new StackPanel { Spacing = 1 };
        patCol.Children.Add(Dashboard.BuildSubHeader("PATTERN", _accent));
        patCol.Children.Add(Dashboard.BuildDataRow("Tempo", _tempoLabel));
        patCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.Leng));
        patCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.Shuffle));
        patCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.Level));
        Grid.SetColumn(patCol, 0); grid.Children.Add(patCol);

        var arpCol = new StackPanel { Spacing = 1 };
        arpCol.Children.Add(Dashboard.BuildSubHeader("ARPEGGIATOR", _accent));
        arpCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.ArpType));
        arpCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.ArpRate));
        Grid.SetColumn(arpCol, 1); grid.Children.Add(arpCol);

        var motCol = new StackPanel { Spacing = 1 };
        motCol.Children.Add(Dashboard.BuildSubHeader("AUTOMATION", _accent));
        var laneGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing     = 6,
        };
        for (int i = 0; i < 8; i++)
        {
            var row = Dashboard.BuildDataRow($"Lane {i + 1}", _motionCcLabels[i]);
            Grid.SetRow(row, i / 2);
            Grid.SetColumn(row, i % 2);
            if (laneGrid.RowDefinitions.Count <= i / 2)
                laneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            laneGrid.Children.Add(row);
        }
        motCol.Children.Add(laneGrid);
        Grid.SetColumn(motCol, 2); grid.Children.Add(motCol);

        var divider = new Border
        {
            Width      = 1,
            Background = Palette.BdCard,
            Margin     = new Thickness(4, 18, 4, 4),
        };
        Grid.SetColumn(divider, 3); grid.Children.Add(divider);

        var dmCol = new StackPanel { Spacing = 1 };
        dmCol.Children.Add(Dashboard.BuildSubHeader("D-MOTION", _dmAccent));
        dmCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.DmAssignX));
        dmCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.DmAssignY));
        Grid.SetColumn(dmCol, 4); grid.Children.Add(dmCol);

        // Header with right-aligned VIEW STEPS → button.
        var viewStepsLabel = new TextBlock
        {
            Text          = "VIEW STEPS  →",
            FontSize      = 9.5,
            FontWeight    = FontWeight.SemiBold,
            LetterSpacing = 0.8,
            Foreground    = _accent,
        };
        var viewStepsBtn = new Border
        {
            BorderThickness     = new Thickness(1),
            BorderBrush         = _accent,
            Background          = new SolidColorBrush(Color.FromArgb(0x18, accentClr.R, accentClr.G, accentClr.B)),
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(8, 3),
            Cursor              = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            Child               = viewStepsLabel,
        };

        void RefreshViewBtn()
        {
            bool has = HasSequencerContent(_prm);
            viewStepsBtn.IsEnabled = has;
            viewStepsBtn.Opacity   = has ? 1.0 : 0.35;
        }
        RefreshViewBtn();
        _prm.Sequence.DataChanged += (_, _) => Dispatcher.UIThread.Post(RefreshViewBtn);
        viewStepsBtn.PointerPressed += (_, _) => _openSequencer();

        var seqAccentDot = new Ellipse
        {
            Width = 6, Height = 6, Fill = _accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var seqTitle = new TextBlock
        {
            Text              = "SEQUENCER",
            FontSize          = 8.5,
            FontWeight        = FontWeight.Bold,
            LetterSpacing     = 1.5,
            Foreground        = _accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0, 0, 0),
        };
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children    = { seqAccentDot, seqTitle },
        };
        Grid.SetColumn(headerLeft, 0);   headerGrid.Children.Add(headerLeft);
        Grid.SetColumn(viewStepsBtn, 2); headerGrid.Children.Add(viewStepsBtn);

        var headerUnderline = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 8),
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

        var body = new StackPanel { Children = { headerGrid, headerUnderline, grid } };
        return new Border
        {
            Background      = Palette.BgCard,
            BorderBrush     = Palette.BdCard,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(8, 6),
            Child           = body,
        };
    }

    // Pure check against the PRM data — used to enable/disable the VIEW STEPS button.
    public static bool HasSequencerContent(PrmFileManager prm)
    {
        int count = prm.Sequence.StepCount;
        for (int s = 0; s < count; s++)
        {
            var step = prm.Sequence.Steps[s];
            if (step.Notes.Any(n => n >= 0))   return true;
            if (step.Motions.Any(m => m >= 0)) return true;
            if (step.PitchBend != -32768)       return true;
        }
        return false;
    }
}
