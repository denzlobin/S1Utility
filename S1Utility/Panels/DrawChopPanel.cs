using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// DRAW · CHOP card body: three knobs (Draw Multiply / Chop Comb / Chop Overtone)
// and one LED group (Step/Slope). Shares the OSC accent — sits in column 0
// directly under the OSCILLATOR card.
internal sealed class DrawChopPanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;

    public DrawChopPanel(S1Patch patch, IBrush accent)
    {
        _patch  = patch;
        _accent = accent;
    }

    public void Populate(Panel target)
    {
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        knobs.Children.Add(Knobs.MakeKnob(_patch, Cc(102), _accent, minCcValue: 3));
        knobs.Children.Add(Knobs.MakeKnob(_patch, Cc(104), _accent, minCcValue: 3));
        knobs.Children.Add(Knobs.MakeKnob(_patch, Cc(103), _accent));
        target.Children.Add(knobs);

        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(107), _accent));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
