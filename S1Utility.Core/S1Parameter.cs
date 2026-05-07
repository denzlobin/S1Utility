using System;
using System.Threading;

namespace S1Utility.Core;

// How the parameter should be displayed in the UI.
public enum S1ParameterType
{
    Continuous,     // 0-127 â€” use a rotary knob
    Toggle,         // on/off â€” use a checkbox (sends 0 or 127)
    Dropdown,       // named options â€” use a ComboBox (value = option index)
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
    // Non-null for Dropdown parameters â€” one string per selectable option.
    public string[]?       Options       { get; }

    private int _value;

    // â”€â”€ Sync state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // 0 = unsynced, 1 = synced. Manipulated with Interlocked so it is safe to
    // write from the MIDI receive thread and read from the UI thread.
    private int _syncedFlag;

    // True once we know the synth's value for this parameter matches the editor.
    // Starts false; transitions to true via MarkSynced() â€” never resets in a session.
    public bool IsSynced => _syncedFlag == 1;

    // Fires exactly once per parameter when it transitions unsynced â†’ synced.
    // May fire from any thread; subscribers must dispatch to UI if needed.
    public event EventHandler<bool>? SyncStateChanged;

    public void MarkSynced()
    {
        if (Interlocked.Exchange(ref _syncedFlag, 1) == 0)
            SyncStateChanged?.Invoke(this, true);
    }

    internal void ResetSync()
    {
        if (Interlocked.Exchange(ref _syncedFlag, 0) == 1)
            SyncStateChanged?.Invoke(this, false);
    }

    // Setting Value from the UI: clamps, sends CC to hardware, notifies UI.
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 127);
            if (clamped == _value) return;
            _value = clamped;
            _onSend?.Invoke(this);          // â†’ sends CC out to hardware
            ValueChanged?.Invoke(this, _value); // â†’ updates UI controls
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
        ValueChanged?.Invoke(this, _value); // â†’ updates UI only, no send
    }

    // Wired by S1Patch.ConnectAsync to trigger outgoing CC sends.
    internal Action<S1Parameter>? _onSend;

    // Subscribed by UI controls (knobs, toggles) to stay in sync with the model.
    // Fires on both UI-driven changes and incoming MIDI â€” subscribers must not
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
