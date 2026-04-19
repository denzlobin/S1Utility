using System;

namespace RolandS1Editor;

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

    // Fires with the waveform index whenever a waveform's full pattern is replaced
    // (SetAll or LoadFromPrm). Individual SetStep calls do not fire this event.
    public event Action<int>? PatternChanged;

    public bool GetStep(int waveform, int step) => _steps[waveform][step];

    public void SetStep(int waveform, int step, bool on) =>
        _steps[waveform][step] = on;

    public void SetAll(int waveform, bool on)
    {
        Array.Fill(_steps[waveform], on);
        PatternChanged?.Invoke(waveform);
    }

    // Decode a raw PRM integer: bit (N-1) = step N.
    public void LoadFromPrm(int waveform, int rawValue)
    {
        for (int s = 0; s < Steps; s++)
            _steps[waveform][s] = ((rawValue >> s) & 1) == 1;
        PatternChanged?.Invoke(waveform);
    }

    // Encode current steps back to a PRM integer.
    public int ToPrm(int waveform)
    {
        int result = 0;
        for (int s = 0; s < Steps; s++)
            if (_steps[waveform][s])
                result |= 1 << s;
        return result;
    }
}
