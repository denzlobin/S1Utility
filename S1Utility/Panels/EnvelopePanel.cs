using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// Envelope card body: ADSR visualization + knob row + amp-env-mode / trigger-mode LED pair.
internal sealed class EnvelopePanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;
    private readonly System.Func<S1Parameter, S1Parameter, S1Parameter, S1Parameter, Control> _makeAdsr;

    public EnvelopePanel(
        S1Patch patch,
        IBrush accent,
        System.Func<S1Parameter, S1Parameter, S1Parameter, S1Parameter, Control> makeAdsr)
    {
        _patch    = patch;
        _accent   = accent;
        _makeAdsr = makeAdsr;
    }

    public void Populate(Panel target)
    {
        target.Children.Add(_makeAdsr(Cc(73), Cc(75), Cc(30), Cc(72)));

        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 73, 75, 30, 72 })
            knobs.Children.Add(Knobs.MakeKnob(_patch, Cc(cc), _accent));
        target.Children.Add(knobs);

        var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var ampBtn = Knobs.MakeLedButtonGroup(_patch, Cc(28), _accent);
        var trgBtn = Knobs.MakeLedButtonGroup(_patch, Cc(29), _accent);
        Grid.SetColumn(ampBtn, 0); Grid.SetColumn(trgBtn, 1);
        btnGrid.Children.Add(ampBtn); btnGrid.Children.Add(trgBtn);
        target.Children.Add(btnGrid);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
