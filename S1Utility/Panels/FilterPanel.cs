using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// Filter card body: response curve visualization + two knob rows.
internal sealed class FilterPanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;
    private readonly System.Func<S1Parameter, S1Parameter, Control> _makeFilterCurve;

    public FilterPanel(S1Patch patch, IBrush accent, System.Func<S1Parameter, S1Parameter, Control> makeFilterCurve)
    {
        _patch           = patch;
        _accent          = accent;
        _makeFilterCurve = makeFilterCurve;
    }

    public void Populate(Panel target)
    {
        target.Children.Add(_makeFilterCurve(Cc(74), Cc(71)));

        var filtRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 74, 71, 24 })
            filtRow1.Children.Add(Knobs.MakeKnob(_patch, Cc(cc), _accent));
        target.Children.Add(filtRow1);

        var filtRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 25, 26, 27 })
            filtRow2.Children.Add(Knobs.MakeKnob(_patch, Cc(cc), _accent));
        target.Children.Add(filtRow2);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
