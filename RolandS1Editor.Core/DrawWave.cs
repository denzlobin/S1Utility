using System;

namespace RolandS1Editor;

// Holds the 8 draw-point values loaded from the .PRM file.
// Each OSC_DRAW_P1..P8 is stored as an unsigned 16-bit integer in the PRM
// but represents a signed amplitude (-32768..32767) for display purposes.
// The S-1's 16 pads act as +/- controls for each of the 8 positions.
public class DrawWave
{
    public const int Points = 8;

    private readonly int[] _signed = new int[Points]; // -32768..32767

    public int GetPoint(int index) => _signed[index];

    public void LoadAll(int[] rawPrmValues)
    {
        for (int i = 0; i < Points && i < rawPrmValues.Length; i++)
        {
            int raw = rawPrmValues[i];
            _signed[i] = raw > 32767 ? raw - 65536 : raw;
        }
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? PointsChanged;
}
