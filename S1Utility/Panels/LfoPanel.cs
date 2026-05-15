using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// LFO card body: rate knob (sync-aware) + mod-depth knob + waveform LED + 3 mode LEDs.
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

        var modeRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 2,
            Margin              = new Thickness(3, 1, 3, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        modeRow.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(79),  _accent));
        modeRow.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(106), _accent));
        modeRow.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(105), _accent));
        target.Children.Add(modeRow);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
