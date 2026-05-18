using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// OSCILLATOR card — 12 params in a 2-column grid. Inspector-only labels
// disambiguate the level/range/mode fields from Tab 1 editor's terser names.
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
        // Row 1 Square/Saw Level · Row 2 Sub/Noise Level · Row 3 Osc Range/Fine
        // Tune · Row 4 Square PW/PWM Source · Row 5 Sub Oct Type/Noise Mode ·
        // Row 6 LFO Pitch/Bend Amount.
        var rows = new Control[]
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(19), "Square Level"),
            InspectorRows.BuildCcDataRow(_prm, Cc(20), "Saw Level"),
            InspectorRows.BuildCcDataRow(_prm, Cc(21), "Sub Level"),
            InspectorRows.BuildCcDataRow(_prm, Cc(23), "Noise Level"),
            InspectorRows.BuildCcDataRow(_prm, Cc(14), "Osc Range"),
            InspectorRows.BuildCcDataRow(_prm, Cc(76)),                  // Fine Tune
            InspectorRows.BuildCcDataRow(_prm, Cc(15)),                  // Square PW
            InspectorRows.BuildCcDataRow(_prm, Cc(16)),                  // PWM Source
            InspectorRows.BuildCcDataRow(_prm, Cc(22)),                  // Sub Oct Type
            InspectorRows.BuildCcDataRow(_prm, Cc(78)),                  // Noise Mode
            InspectorRows.BuildCcDataRow(_prm, Cc(13)),                  // LFO Pitch
            InspectorRows.BuildCcDataRow(_prm, Cc(18)),                  // Bend Amount
        };
        return Dashboard.BuildPrmCard("OSCILLATOR", _accent, Dashboard.BuildTwoColGrid(rows));
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
