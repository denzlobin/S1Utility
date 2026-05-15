using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// VOICE inspector card — main params in a 3-col grid, then CHORD sub-section
// (V2/V3/V4 toggles + key shifts) in its own 3-col grid below.
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
        // Row-major fill of a 3-col grid (last cell empty since we have 8 params):
        // Col 1 = Polyphony/Portamento/Glide · Col 2 = Mod Wheel/Exp/Damper · Col 3 = Transpose/Pan.
        int[] mainOrder = { 80, 1, 77,    31, 11, 10,    5, 64 };
        var mainItems = mainOrder.Select(cc => InspectorRows.BuildCcDataRow(_prm, Cc(cc))).ToList();

        var chordItems = new List<Control>
        {
            InspectorRows.BuildCcDataRow(_prm, Cc(81)),  // V2
            InspectorRows.BuildCcDataRow(_prm, Cc(82)),  // V3
            InspectorRows.BuildCcDataRow(_prm, Cc(83)),  // V4
            InspectorRows.BuildCcDataRow(_prm, Cc(85)),  // V2 Shift
            InspectorRows.BuildCcDataRow(_prm, Cc(86)),  // V3 Shift
            InspectorRows.BuildCcDataRow(_prm, Cc(87)),  // V4 Shift
        };

        var body = new StackPanel
        {
            Children =
            {
                Dashboard.BuildThreeColGrid(mainItems),
                Dashboard.BuildSubHeader("CHORD", _accent),
                Dashboard.BuildThreeColGrid(chordItems),
            },
        };
        return Dashboard.BuildPrmCard("VOICE", _accent, body);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
