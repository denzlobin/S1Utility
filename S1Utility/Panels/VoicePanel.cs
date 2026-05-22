using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// Voice card body: 5 knob row + portamento/polyphony LEDs + drone button.
// CHORD lives in its own top-level card; see ChordPanel.
internal sealed class VoicePanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;

    public VoicePanel(S1Patch patch, IBrush accent)
    {
        _patch  = patch;
        _accent = accent;
    }

    public void Populate(Panel target)
    {
        // 5 knobs × 56px = 280px + margins ≈ 300px — fits the column without overflow
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 1, 11, 5, 10, 77 })
            knobs.Children.Add(Knobs.MakeKnob(_patch, Cc(cc), _accent, containerWidth: 56));
        target.Children.Add(knobs);

        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(31), _accent));
        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(80), _accent));

        target.Children.Add(Knobs.MakeDroneButton(_patch, Cc(64), _accent));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
