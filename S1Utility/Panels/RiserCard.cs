using Avalonia.Controls;
using Avalonia.Media;
using S1Utility.Widgets;

namespace S1Utility.Panels;

// RISER inspector card — PRM-only effect (no MIDI path). Surfaces Mode,
// Resonance, Shape, Level. RiserSw / RiserCtrl / RiserBeat omitted: SW is
// always Off on the device and the other two have no observed effect.
internal sealed class RiserCard
{
    private readonly PrmFileManager _prm;
    private readonly IBrush _accent;

    public RiserCard(PrmFileManager prm, IBrush accent)
    {
        _prm    = prm;
        _accent = accent;
    }

    public Border Build()
    {
        var rows = new Control[]
        {
            InspectorRows.BuildPrmDataRow(_prm.RiserMode),
            InspectorRows.BuildPrmDataRow(_prm.RiserReso),
            InspectorRows.BuildPrmDataRow(_prm.RiserShape),
            InspectorRows.BuildPrmDataRow(_prm.RiserLevel),
        };
        return Dashboard.BuildPrmCard("RISER", _accent, Dashboard.BuildTwoColGrid(rows));
    }
}
