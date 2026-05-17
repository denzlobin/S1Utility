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

// Sequencer inspector cards — split visually into:
//   1. SEQUENCER: PATTERN + ARPEGGIATOR, with a "VIEW STEPS →" header button
//      that opens the dedicated sequencer window.
//   2. MOTION: AUTOMATION lanes + D-MOTION assigns.
// Tempo and motion-lane labels are owned by MainWindow (it sets their Text on
// PrmMetaLoaded); they're passed in so the cards just place them.
internal sealed class SequencerCard
{
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;
    private readonly IBrush _dmAccent;
    private readonly TextBlock _tempoLabel;
    private readonly TextBlock[] _motionCcLabels;
    private readonly Action _openSequencer;
    private readonly Action _exportMidi;

    public SequencerCard(
        PrmFileManager prm,
        IBrush accent,
        IBrush dmAccent,
        TextBlock tempoLabel,
        TextBlock[] motionCcLabels,
        Action openSequencer,
        Action exportMidi)
    {
        _prm            = prm;
        _accent         = accent;
        _dmAccent       = dmAccent;
        _tempoLabel     = tempoLabel;
        _motionCcLabels = motionCcLabels;
        _openSequencer  = openSequencer;
        _exportMidi     = exportMidi;
    }

    public Border BuildPatternCard()
    {
        var accentClr = ((ISolidColorBrush)_accent).Color;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
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

        // VIEW STEPS + EXPORT MIDI buttons sit side-by-side at the bottom of
        // the ARPEGGIATOR column (the shorter of the two), visually separated
        // by a top margin from the ARP rows.
        var viewStepsBtn  = MakeActionButton("VIEW STEPS",  accentClr);
        var exportMidiBtn = MakeActionButton("EXPORT MIDI",   accentClr);

        void RefreshButtons()
        {
            bool has = HasSequencerContent(_prm);
            viewStepsBtn.IsEnabled  = has;
            viewStepsBtn.Opacity    = has ? 1.0 : 0.35;
            exportMidiBtn.IsEnabled = has;
            exportMidiBtn.Opacity   = has ? 1.0 : 0.35;
        }
        RefreshButtons();
        _prm.Sequence.DataChanged += (_, _) => Dispatcher.UIThread.Post(RefreshButtons);
        viewStepsBtn.PointerPressed  += (_, _) => _openSequencer();
        exportMidiBtn.PointerPressed += (_, _) => _exportMidi();

        var buttonRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing     = 6,
            Margin            = new Thickness(0, 14, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(viewStepsBtn,  0); buttonRow.Children.Add(viewStepsBtn);
        Grid.SetColumn(exportMidiBtn, 1); buttonRow.Children.Add(exportMidiBtn);
        arpCol.Children.Add(buttonRow);
        Grid.SetColumn(arpCol, 1); grid.Children.Add(arpCol);

        return WrapInCard("SEQUENCER", _accent, grid, headerRightContent: null);
    }

    public Border BuildMotionCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing     = 10,
        };

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
        Grid.SetColumn(motCol, 0); grid.Children.Add(motCol);

        var divider = new Border
        {
            Width      = 1,
            Background = Palette.BdCard,
            Margin     = new Thickness(4, 18, 4, 4),
        };
        Grid.SetColumn(divider, 1); grid.Children.Add(divider);

        var dmCol = new StackPanel { Spacing = 1 };
        dmCol.Children.Add(Dashboard.BuildSubHeader("D-MOTION", _dmAccent));
        dmCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.DmAssignX));
        dmCol.Children.Add(InspectorRows.BuildPrmDataRow(_prm.DmAssignY));
        Grid.SetColumn(dmCol, 2); grid.Children.Add(dmCol);

        return WrapInCard("MOTION", _accent, grid, headerRightContent: null);
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

    private Border MakeActionButton(string text, Color accentClr) => new()
    {
        BorderThickness     = new Thickness(1),
        BorderBrush         = _accent,
        Background          = new SolidColorBrush(Color.FromArgb(0x18, accentClr.R, accentClr.G, accentClr.B)),
        CornerRadius        = new CornerRadius(3),
        Padding             = new Thickness(8, 4),
        Cursor              = new Cursor(StandardCursorType.Hand),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = new TextBlock
        {
            Text                = text,
            FontSize            = 9.5,
            FontWeight          = FontWeight.SemiBold,
            LetterSpacing       = 0.8,
            Foreground          = _accent,
            HorizontalAlignment = HorizontalAlignment.Center,
        },
    };

    // Card chrome with an optional right-aligned header element (e.g. VIEW STEPS button).
    private static Border WrapInCard(string title, IBrush accent, Control body, Control? headerRightContent)
    {
        var accentClr = ((ISolidColorBrush)accent).Color;

        var dot = new Ellipse
        {
            Width = 6, Height = 6, Fill = accent,
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
        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children    = { dot, titleText },
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        Grid.SetColumn(headerLeft, 0); headerGrid.Children.Add(headerLeft);
        if (headerRightContent is not null)
        {
            Grid.SetColumn(headerRightContent, 2);
            headerGrid.Children.Add(headerRightContent);
        }

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

        var stack = new StackPanel { Children = { headerGrid, headerUnderline, body } };
        return new Border
        {
            Background      = Palette.BgCard,
            BorderBrush     = Palette.BdCard,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(8, 6),
            Child           = stack,
        };
    }
}
