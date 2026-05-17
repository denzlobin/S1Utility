using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// OSC card body: waveform preview, two knob rows, four LED button strips.
// DRAW · CHOP lives in its own top-level card; see DrawChopPanel.
internal sealed class OscPanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;
    private readonly System.Func<Control> _makeOscWaveform;

    public OscPanel(S1Patch patch, IBrush accent, System.Func<Control> makeOscWaveform)
    {
        _patch           = patch;
        _accent          = accent;
        _makeOscWaveform = makeOscWaveform;
    }

    public void Populate(Panel target)
    {
        target.Children.Add(_makeOscWaveform());

        // Row 1: level knobs
        var knobRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 19, 20, 21, 23 })
            knobRow1.Children.Add(Knobs.MakeKnob(_patch, Cc(cc), _accent));
        target.Children.Add(knobRow1);

        // Row 2: modulation / tuning knobs
        var knobRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 15, 13, 76, 18 })
            knobRow2.Children.Add(Knobs.MakeKnob(_patch, Cc(cc), _accent));
        target.Children.Add(knobRow2);

        // Button strips stacked vertically — all full-width, uniform button cells per strip
        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(14), _accent));  // Range
        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(78), _accent));  // Noise Mode
        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(16), _accent));  // PWM Source
        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(22), _accent));  // Sub Octave

    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
