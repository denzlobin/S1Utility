using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// CHORD card body: three chord voice rows (interval + key shift slider each).
// Lives in its own top-level card with the VOICE accent. The outer card Border
// is dimmed/disabled when Polyphony (CC80) != Chord — passed in so the entire
// card visually deactivates, not just the inner rows.
internal sealed class ChordPanel
{
    private readonly S1Patch _patch;
    private readonly IBrush _accent;
    private readonly Control _outerCard;

    public ChordPanel(S1Patch patch, IBrush accent, Control outerCard)
    {
        _patch     = patch;
        _accent    = accent;
        _outerCard = outerCard;
    }

    public void Populate(Panel target)
    {
        target.Children.Add(Knobs.MakeChordVoiceRow(_patch, 2, Cc(81), Cc(85), _accent));
        target.Children.Add(Knobs.MakeChordVoiceRow(_patch, 3, Cc(82), Cc(86), _accent));
        target.Children.Add(Knobs.MakeChordVoiceRow(_patch, 4, Cc(83), Cc(87), _accent));

        var polyParam = Cc(80);
        void UpdateChordEnabled(int v)
        {
            bool isChord = v == 3;
            _outerCard.IsEnabled = isChord;
            _outerCard.Opacity   = isChord ? 1.0 : 0.3;
        }
        UpdateChordEnabled(polyParam.Value);
        polyParam.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() => UpdateChordEnabled(v));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
