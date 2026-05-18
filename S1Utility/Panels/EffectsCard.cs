using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// EFFECTS inspector card: two columns (Reverb + Chorus on the left, Delay on
// the right). Delay's Time row is context-aware — ms when DELAY_SW = Off
// (free), tempo division when Sync to Tempo.
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
            InspectorRows.BuildPrmDataRow(_prm.ReverbAdv[1]), // Density
            InspectorRows.BuildPrmDataRow(_prm.ReverbAdv[2]), // Low Cut
            InspectorRows.BuildPrmDataRow(_prm.ReverbAdv[3]), // High Cut
        });

        var delItems = new List<Control>
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(92)),       // Level
            BuildDelayTimeRow(),                               // Time (context-aware)
            InspectorRows.BuildPrmDataRow(_prm.DelayMain[0]), // Sync (DELAY_SW)
            InspectorRows.BuildPrmDataRow(_prm.DelayTempo),
            InspectorRows.BuildPrmDataRow(_prm.DelayAdv[0]),  // Feedback
            InspectorRows.BuildPrmDataRow(_prm.DelayAdv[1]),  // Low Cut
            InspectorRows.BuildPrmDataRow(_prm.DelayAdv[2]),  // High Cut
        };
        var delCol = Dashboard.BuildEffectsSubCol("DELAY", _accent, delItems);

        var chorusCol = Dashboard.BuildEffectsSubCol("CHORUS", _accent, new[]
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(93)),       // Type
        });

        var col1 = new StackPanel
        {
            Spacing  = 6,
            Children = { revCol, chorusCol },
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing     = 14,
        };
        Grid.SetColumn(col1,  0); grid.Children.Add(col1);
        Grid.SetColumn(delCol, 1); grid.Children.Add(delCol);

        return Dashboard.BuildPrmCard("EFFECTS", _accent, grid);
    }

    private Control BuildDelayTimeRow()
    {
        var delaySw = _prm.DelayMain[0];

        var lbl = new TextBlock();
        void Refresh()
        {
            // DELAY_SW: 1 = Off (free ms), 0 = Sync to Tempo. Free mode reads
            // the raw 8-bit DELAY_TIME so the ms anchors stay precise — the
            // CC snapshot is 7-bit and rounds two raw values into one.
            if (delaySw.Value == 1)
            {
                int raw = _prm.PrmRawSnapshot.TryGetValue("DELAY_TIME", out var v) ? v : 0;
                lbl.Text = $"{DelayTimeMap.RawToMs(raw)}ms";
            }
            else
            {
                lbl.Text = CcDisplay.PrmValue(_prm.DelayTempo);
            }
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
