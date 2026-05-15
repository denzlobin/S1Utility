using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// OSCILLATOR card — 12 simple params in a 3-column grid. Row-major fill so the
// columns read top-to-bottom as designed.
internal sealed class OscillatorCard
{
    private readonly S1Patch _patch;
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public OscillatorCard(S1Patch patch, PrmFileManager prm, IBrush accent)
    {
        _patch  = patch;
        _prm    = prm;
        _accent = accent;
    }

    public Border Build()
    {
        // Col 1 = Square/Saw/Sub/Noise · Col 2 = Range/PW/PWM Src/Sub Oct · Col 3 = LFO Pitch/Noise/Fine/Bend.
        int[] order = { 19, 14, 13,    20, 15, 78,    21, 16, 76,    23, 22, 18 };
        var rows = order.Select(cc => InspectorRows.BuildCcDataRow(_prm, Cc(cc)));
        return Dashboard.BuildPrmCard("OSCILLATOR", _accent, Dashboard.BuildThreeColGrid(rows));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
