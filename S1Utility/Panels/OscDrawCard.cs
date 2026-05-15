using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// OSC DRAW card — bipolar 16-bar waveform + Multiply + Step/Slope rows.
internal sealed class OscDrawCard
{
    private readonly S1Patch _patch;
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public OscDrawCard(S1Patch patch, PrmFileManager prm, IBrush accent)
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
                PrmVisualizers.MakeDrawBarsControl(_prm, _accent),
                InspectorRows.BuildCcDataRow(_prm, Cc(102)),  // Multiply
                InspectorRows.BuildCcDataRow(_prm, Cc(107)),  // Step/Slope
            },
        };
        return Dashboard.BuildPrmCard("OSC DRAW", _accent, body);
    }

    private S1Parameter Cc(int cc) =>
        _patch.GetByCC(cc) ?? throw new System.InvalidOperationException($"Required parameter CC {cc} not found in patch");
}
