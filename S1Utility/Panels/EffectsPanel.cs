using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// Effects card body: Reverb (level + time) | divider | Delay (level + time-or-tempo) + Chorus LED.
internal sealed class EffectsPanel
{
    private readonly S1Patch _patch;
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public EffectsPanel(S1Patch patch, PrmFileManager prm, IBrush accent)
    {
        _patch  = patch;
        _prm    = prm;
        _accent = accent;
    }

    public void Populate(Panel target)
    {
        // Reverb + Delay side by side
        var sideBySide = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Margin            = new Thickness(0, 0, 0, 2),
        };
        var reverbCol = new StackPanel();
        reverbCol.Children.Add(Dashboard.BuildSubHeader("REVERB", _accent));
        var revKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        revKnobs.Children.Add(Knobs.MakeKnob(_patch, Cc(91), _accent));
        revKnobs.Children.Add(Knobs.MakeKnob(_patch, Cc(89), _accent));
        reverbCol.Children.Add(revKnobs);

        var divider = new Border
        {
            Width      = 1,
            Background = new SolidColorBrush(Color.Parse("#2E2E2E")),
            Margin     = new Thickness(2, 0),
        };

        var delayCol = new StackPanel();
        delayCol.Children.Add(Dashboard.BuildSubHeader("DELAY", _accent));
        var delKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        delKnobs.Children.Add(Knobs.MakeKnob(_patch, Cc(92), _accent));
        delKnobs.Children.Add(Knobs.MakeDelayTimeKnob(_patch, _prm.TempoSync, _prm.DelayTempo, _accent));
        delayCol.Children.Add(delKnobs);

        Grid.SetColumn(reverbCol, 0);
        Grid.SetColumn(divider,   1);
        Grid.SetColumn(delayCol,  2);
        sideBySide.Children.Add(reverbCol);
        sideBySide.Children.Add(divider);
        sideBySide.Children.Add(delayCol);
        target.Children.Add(sideBySide);

        target.Children.Add(Dashboard.BuildSubHeader("CHORUS", _accent));
        target.Children.Add(Knobs.MakeLedButtonGroup(_patch, Cc(93), _accent));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
