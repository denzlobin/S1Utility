using System;

namespace RolandS1Editor;

// A parameter that lives only in the .PRM file — no MIDI CC is sent when it changes.
// Knobs store value 0-127 (knob range); ToPrm/LoadFromPrm scale to/from the native PRM range.
// Dropdowns store the option index directly, which equals the PRM raw value.
public class PrmParameter
{
    public string    Name    { get; }
    public string    PrmKey  { get; }
    public int       PrmMax  { get; }   // native PRM max; used for knob scaling only
    public string[]? Options { get; }   // non-null → dropdown (value = option index)

    private int _value;

    public int Value
    {
        get => _value;
        set
        {
            var max     = Options is not null ? Options.Length - 1 : 127;
            var clamped = Math.Clamp(value, 0, max);
            if (clamped == _value) return;
            _value = clamped;
            ValueChanged?.Invoke(this, _value);
        }
    }

    // Scale current Value to the raw PRM integer for file output.
    public int ToPrm() =>
        Options is not null
            ? Value
            : (int)Math.Round((double)Value * PrmMax / 127.0);

    // Set Value from a raw PRM integer (e.g. when loading a file).
    public void LoadFromPrm(int raw)
    {
        Value = Options is not null
            ? Math.Clamp(raw, 0, Options.Length - 1)
            : (int)Math.Round((double)Math.Clamp(raw, 0, PrmMax) * 127.0 / PrmMax);
    }

    public event EventHandler<int>? ValueChanged;

    // prmMax: the maximum raw PRM value (used for knob scaling). Ignored for dropdowns.
    public PrmParameter(string name, string prmKey, int prmMax = 127,
        string[]? options = null, int initialValue = 0)
    {
        Name    = name;
        PrmKey  = prmKey;
        PrmMax  = prmMax;
        Options = options;
        _value  = Math.Clamp(initialValue, 0, options is not null ? options.Length - 1 : 127);
    }
}
