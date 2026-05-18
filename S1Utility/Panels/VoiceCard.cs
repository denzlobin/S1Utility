using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// VOICE inspector card — main params in a 2-col grid, then CHORD sub-section
// (V2/V3/V4 toggles + key shifts) in its own 3-col grid below. CHORD stays
// 3-col to keep the card short enough that RISER fits below in colB.
internal sealed class VoiceCard
{
    private readonly S1Patch _patch;
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public VoiceCard(S1Patch patch, PrmFileManager prm, IBrush accent)
    {
        _patch  = patch;
        _prm    = prm;
        _accent = accent;
    }

    public Border Build()
    {
        // Single row: Polyphony · Portamento · Portamento Time (inspector
        // label; canonical S1Parameter name is "Glide Time").
        var mainItems = new List<Control>
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(80)),
            InspectorRows.BuildCcDataRow(_prm, Cc(31)),
            InspectorRows.BuildCcDataRow(_prm, Cc(5), "Portamento Time"),
        };

        var chordItems = new List<Control>
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(81), "Voice 2"),
            InspectorRows.BuildCcDataRow(_prm, Cc(82), "Voice 3"),
            InspectorRows.BuildCcDataRow(_prm, Cc(83), "Voice 4"),
            InspectorRows.BuildCcDataRow(_prm, Cc(85), "Voice 2 Key Shift"),
            InspectorRows.BuildCcDataRow(_prm, Cc(86), "Voice 3 Key Shift"),
            InspectorRows.BuildCcDataRow(_prm, Cc(87), "Voice 4 Key Shift"),
        };

        var body = new StackPanel
        {
            Children =
            {
                Dashboard.BuildTwoColGrid(mainItems),
                Dashboard.BuildSubHeader("CHORD", _accent),
                // 3-col keeps the card short so RISER fits below Voice in colB;
                // row 0 = V2/V3/V4 levels, row 1 = matching key shifts.
                Dashboard.BuildThreeColGrid(chordItems),
            },
        };
        return Dashboard.BuildPrmCard("VOICE", _accent, body);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
