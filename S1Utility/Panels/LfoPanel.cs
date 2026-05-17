using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// LFO card body: rate knob (sync-aware) + mod-depth knob + waveform LED strip +
// 4-cell mode row (NORMAL/FAST radio + SYNC/KEY TRIG toggles). NORMAL/FAST dim
// while Sync is on but preserve their value.
internal sealed class LfoPanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;

    public LfoPanel(S1Patch patch, IBrush accent)
    {
        _patch  = patch;
        _accent = accent;
    }

    public void Populate(Panel target)
    {
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        knobs.Children.Add(Knobs.MakeLfoRateKnob(_patch, _accent));
        knobs.Children.Add(Knobs.MakeKnob(_patch, Cc(17), _accent));
        target.Children.Add(knobs);

        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(12), _accent));

        var modeP    = Cc(79);
        var syncP    = Cc(106);
        var keyTrigP = Cc(105);

        // NORMAL/FAST select the Mode param value, but only matter when Sync is
        // off. While Sync is on, they dim and ignore clicks — the underlying
        // modeP value is preserved so toggling Sync back off restores the
        // previously chosen Mode visually.
        bool ModeEnabled() => syncP.Value == 0;

        target.Children.Add(Knobs.MakeMixedToggleRow(_patch, _accent,
            new Knobs.ToggleCell("NORMAL",   modeP,    () => modeP.Value == 0,  () => modeP.Value = 0,  ModeEnabled, syncP),
            new Knobs.ToggleCell("FAST",     modeP,    () => modeP.Value == 1,  () => modeP.Value = 1,  ModeEnabled, syncP),
            new Knobs.ToggleCell("SYNC",     syncP,    () => syncP.Value > 0,   () => syncP.Value = syncP.Value > 0 ? 0 : 127),
            new Knobs.ToggleCell("KEY TRIG", keyTrigP, () => keyTrigP.Value > 0, () => keyTrigP.Value = keyTrigP.Value > 0 ? 0 : 127)
        ));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
