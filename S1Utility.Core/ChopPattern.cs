using System;

namespace S1Utility.Core;

// Holds the 4 × 16 chop step patterns for the S-1's oscillator chop feature.
// Each pattern is one 16-bit integer in the PRM file: bit N-1 = step N ON/OFF.
// There are no MIDI CCs for these — they only take effect when saved to .PRM.
public class ChopPattern
{
    public const int Waveforms = 4;
    public const int Steps     = 16;

    public static readonly string[] WaveformNames = ["Square", "Saw", "Sub", "Noise"];
    public static readonly string[] PrmKeys       = ["OSC_CHOP_PWM", "OSC_CHOP_SAW", "OSC_CHOP_SUB", "OSC_CHOP_NOISE"];

    private readonly bool[][] _steps =
    [
        new bool[Steps], new bool[Steps], new bool[Steps], new bool[Steps]
    ];

    // Fires with the waveform index whenever a waveform's pattern is replaced by LoadFromPrm.
    public event EventHandler<int>? PatternChanged;

    public bool GetStep(int waveform, int step) => _steps[waveform][step];

    // True when at least one step on any waveform is OFF — i.e., the grid
    // differs from the synth's all-bits-on "no chop" default (PRM 0xFFFF for
    // every waveform). Hardware only applies chop overtone when the grid has
    // been altered from this default state.
    public bool IsAltered
    {
        get
        {
            for (int w = 0; w < Waveforms; w++)
                for (int s = 0; s < Steps; s++)
                    if (!_steps[w][s]) return true;
            return false;
        }
    }

    // Decode a raw PRM integer: bit (N-1) = step N.
    public void LoadFromPrm(int waveform, int rawValue)
    {
        for (int s = 0; s < Steps; s++)
            _steps[waveform][s] = ((rawValue >> s) & 1) == 1;
        PatternChanged?.Invoke(this, waveform);
    }
}
