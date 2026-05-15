using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// EFFECTS inspector card: three sub-columns (Reverb / Delay / Chorus). Delay's
// Time row is context-aware (ms when DELAY_SW=Off, tempo-division when On).
internal sealed class EffectsCard
{
    private readonly S1Patch _patch;
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public EffectsCard(S1Patch patch, PrmFileManager prm, IBrush accent)
    {
        _patch  = patch;
        _prm    = prm;
        _accent = accent;
    }

    public Border Build()
    {
        var revCol = Dashboard.BuildEffectsSubCol("REVERB", _accent, new[]
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(91)),       // Level
            InspectorRows.BuildCcDataRow(_prm, Cc(89)),       // Time
            InspectorRows.BuildPrmDataRow(_prm.ReverbMain[0]),// Type
            InspectorRows.BuildPrmDataRow(_prm.ReverbAdv[0]), // Pre-Delay
            InspectorRows.BuildPrmDataRow(_prm.ReverbAdv[1]),
        });

        var delItems = new List<Control>
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(92)),       // Level
            BuildDelayTimeRow(),                               // Time (context-aware)
            InspectorRows.BuildPrmDataRow(_prm.DelayMain[0]), // Sync (DELAY_SW)
            InspectorRows.BuildPrmDataRow(_prm.DelayTempo),
            InspectorRows.BuildPrmDataRow(_prm.DelayAdv[0]),  // Feedback
        };
        var delCol = Dashboard.BuildEffectsSubCol("DELAY", _accent, delItems);

        var chorusCol = Dashboard.BuildEffectsSubCol("CHORUS", _accent, new[]
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(93)),       // Type
        });

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing     = 14,
        };
        Grid.SetColumn(revCol,    0); grid.Children.Add(revCol);
        Grid.SetColumn(delCol,    1); grid.Children.Add(delCol);
        Grid.SetColumn(chorusCol, 2); grid.Children.Add(chorusCol);

        return Dashboard.BuildPrmCard("EFFECTS", _accent, grid);
    }

    private Control BuildDelayTimeRow()
    {
        var delaySw     = _prm.DelayMain[0];
        var delayTimeCC = Cc(90);

        var lbl = new TextBlock();
        void Refresh()
        {
            int delayTimeVal = InspectorRows.SnapshotValue(_prm, delayTimeCC);
            lbl.Text = delaySw.Value == 0
                ? $"{1 + (int)Math.Round(delayTimeVal * 739.0 / 127)}ms"
                : CcDisplay.PrmValue(_prm.DelayTempo);
        }
        Refresh();
        delaySw.ValueChanged         += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _prm.CcSnapshotChanged       += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _prm.DelayTempo.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);

        return Dashboard.BuildDataRow("Time", lbl);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
