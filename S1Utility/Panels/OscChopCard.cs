using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// OSC CHOP card — 16×4 LED matrix + Overtone + Comb rows. PRM-level "Type" /
// "Comb Type" aren't surfaced; the card keeps the canonical CC mapping.
internal sealed class OscChopCard
{
    private readonly S1Patch _patch;
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public OscChopCard(S1Patch patch, PrmFileManager prm, IBrush accent)
    {
        _patch  = patch;
        _prm    = prm;
        _accent = accent;
    }

    public Border Build()
    {
        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                PrmVisualizers.MakeChopPatternControl(_prm, _accent),
                InspectorRows.BuildCcDataRow(_prm, Cc(103)),  // Overtone
                InspectorRows.BuildCcDataRow(_prm, Cc(104)),  // Comb
            },
        };
        return Dashboard.BuildPrmCard("OSC CHOP", _accent, body);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
