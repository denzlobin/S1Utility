using System;
using Avalonia.Controls;
using Avalonia.Threading;
using S1Utility.Core;

namespace S1Utility.Widgets;

// Read-only labeled rows for the Tab 2 inspector. Each row binds to the PRM
// snapshot (CC-mapped rows) or the live PrmParameter (PRM-only rows). Pure
// statics — no MainWindow state captured.
internal static class InspectorRows
{
    // CC-mapped parameter → row that re-renders from the CcSnapshot.
    public static Control BuildCcDataRow(PrmFileManager prm, S1Parameter param)
    {
        var lbl = new TextBlock { Text = FormatCcValue(param, SnapshotValue(prm, param)) };
        prm.CcSnapshotChanged += (_, _) => Dispatcher.UIThread.Post(
            () => lbl.Text = FormatCcValue(param, SnapshotValue(prm, param)));
        return Dashboard.BuildDataRow(param.Name, lbl);
    }

    // LFO Rate has two distinct displays: a 1-31 sync-slot label when LFO_SYNC=1,
    // or a 0-255 free-run value otherwise. PrmFileManager already stores the
    // resolved CC value (0-30 sync index, or 0-127 free), so this row just picks
    // the formatting based on the snapshot's LFO_SYNC value.
    public static Control BuildLfoRateViewerRow(PrmFileManager prm, S1Parameter param)
    {
        var lbl = new TextBlock();
        string Format()
        {
            int cc       = SnapshotValue(prm, param);
            int syncMode = prm.CcSnapshot.TryGetValue(106, out var s) ? s : 0;
            if (syncMode != 0)
            {
                int idx = Math.Clamp(cc, 0, CcDisplay.LfoSyncLabels.Length - 1);
                return CcDisplay.LfoSyncLabels[idx];
            }
            return ((int)Math.Round(cc * 255.0 / 127)).ToString();
        }
        lbl.Text = Format();
        prm.CcSnapshotChanged += (_, _) => Dispatcher.UIThread.Post(() => lbl.Text = Format());
        return Dashboard.BuildDataRow(param.Name, lbl);
    }

    // PRM-only parameter → row that re-renders from the parameter's ValueChanged.
    public static Control BuildPrmDataRow(PrmParameter p)
    {
        var lbl = new TextBlock { Text = CcDisplay.PrmValue(p) };
        p.ValueChanged += (_, _) => Dispatcher.UIThread.Post(() => lbl.Text = CcDisplay.PrmValue(p));
        return Dashboard.BuildDataRow(p.Name, lbl);
    }

    // Snapshot CC value, falling back to the live patch value when the snapshot
    // doesn't have an entry for this CC (happens before the first PRM load).
    public static int SnapshotValue(PrmFileManager prm, S1Parameter param) =>
        prm.CcSnapshot.TryGetValue(param.CcNumber, out var v) ? v : param.Value;

    // Inspector-flavoured display of a CC value: dropdown labels for Options,
    // On/Off for Toggle, ±semitone for BipolarSlider, otherwise the standard
    // knob-value formatting.
    public static string FormatCcValue(S1Parameter param, int value)
    {
        if (param.Options is not null)
        {
            int idx = Math.Clamp(value, 0, param.Options.Length - 1);
            return param.Options[idx];
        }
        return param.ParameterType switch
        {
            S1ParameterType.Toggle        => value > 0 ? "On" : "Off",
            S1ParameterType.BipolarSlider => CcDisplay.FormatSemitone(Math.Clamp(value - 64, -12, 12)),
            _                             => CcDisplay.KnobValue(param, value),
        };
    }
}
