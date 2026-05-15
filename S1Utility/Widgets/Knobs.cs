using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Controls;
using S1Utility.Core;

namespace S1Utility.Widgets;

// Static factories for the standard parameter widgets used by Tab 1 panels.
// Each takes its S1Patch dependency explicitly so they have no `this` capture
// of MainWindow's private state. MarkSynced is gated on patch.IsConnected so
// user drags while offline don't pretend the synth caught up.
internal static class Knobs
{
    public static Control MakeKnob(
        S1Patch patch,
        S1Parameter param,
        IBrush accent,
        int minCcValue = 0,
        double containerWidth = 68)
    {
        string initDisplay = CcDisplay.KnobValue(param);

        var knob = new RotaryKnob
        {
            Value       = param.Value,
            AccentBrush = accent,
            IsSynced    = param.IsSynced,
            MinValue    = minCcValue,
        };
        ToolTip.SetTip(knob, $"{param.Name}: {initDisplay}");

        var valueLabel = new TextBlock
        {
            Classes = { "param-value-label" },
            Text    = param.IsSynced ? initDisplay : "?",
        };

        // Guard: true while param.ValueChanged is pushing a value to the knob so
        // knob.ValueChanged does not treat the programmatic update as a user drag.
        bool updatingFromModel = false;

        knob.ValueChanged += (_, v) =>
        {
            if (updatingFromModel) return;
            param.Value = Math.Max(minCcValue, v);
            if (patch.IsConnected) param.MarkSynced();
            string display = CcDisplay.KnobValue(param);
            ToolTip.SetTip(knob, $"{param.Name}: {display}");
            valueLabel.Text = display;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                updatingFromModel = true;
                knob.Value = v;
                updatingFromModel = false;
                string display = CcDisplay.KnobValue(param);
                ToolTip.SetTip(knob, $"{param.Name}: {display}");
                valueLabel.Text = param.IsSynced ? display : "?";
            });

        param.SyncStateChanged += (_, synced) =>
            Dispatcher.UIThread.Post(() =>
            {
                knob.IsSynced = synced;
                string display = CcDisplay.KnobValue(param);
                valueLabel.Text = synced ? display : "?";
            });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        return Dashboard.MakeKnobContainer(knob, valueLabel, nameLabel, containerWidth);
    }
}
