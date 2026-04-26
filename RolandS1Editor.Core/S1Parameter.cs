using System;

namespace RolandS1Editor;

// How the parameter should be displayed in the UI.
public enum S1ParameterType
{
    Continuous,     // 0-127 — use a rotary knob
    Toggle,         // on/off — use a checkbox (sends 0 or 127)
    Dropdown,       // named options — use a ComboBox (value = option index)
    BipolarSlider,  // signed semitone offset; model stores CC (0-127), center=64
}

// Which section of the S-1's panel this parameter lives on.
public enum S1Section
{
    Controls,
    Lfo,
    Voice,
    Oscillator,
    Filter,
    Envelope,
    Effects
}

// Represents one knob/switch on the S-1, mapped to a single MIDI CC.
public class S1Parameter
{
    public string          Name          { get; }
    public int             CcNumber      { get; }
    public S1Section       Section       { get; }
    public S1ParameterType ParameterType { get; }
    // Non-null for Dropdown parameters — one string per selectable option.
    public string[]?       Options       { get; }

    private int _value;

    // Setting Value from the UI: clamps, sends CC to hardware, notifies UI.
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 127);
            if (clamped == _value) return;
            _value = clamped;
            _onSend?.Invoke(this);          // → sends CC out to hardware
            ValueChanged?.Invoke(this, _value); // → updates UI controls
        }
    }

    // Called by S1Patch when a CC arrives FROM the hardware.
    // Updates the value and notifies the UI, but does NOT send a CC back out
    // (which would create an echo loop).
    internal void UpdateFromMidi(int value)
    {
        var clamped = Math.Clamp(value, 0, 127);
        if (clamped == _value) return;
        _value = clamped;
        ValueChanged?.Invoke(this, _value); // → updates UI only, no send
    }

    // Wired by S1Patch.ConnectAsync to trigger outgoing CC sends.
    internal Action<S1Parameter>? _onSend;

    // Subscribed by UI controls (knobs, toggles) to stay in sync with the model.
    // Fires on both UI-driven changes and incoming MIDI — subscribers must not
    // assume which thread this is called on.
    public event EventHandler<int>? ValueChanged;

    public S1Parameter(string name, int cc, S1Section section,
        int initialValue = 64, S1ParameterType type = S1ParameterType.Continuous,
        string[]? options = null)
    {
        Name          = name;
        CcNumber      = cc;
        Section       = section;
        ParameterType = type;
        Options       = options;
        Value         = initialValue;
    }

    public override string ToString() => $"{Name} (CC{CcNumber}) = {Value}";
}
