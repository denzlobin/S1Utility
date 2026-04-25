using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Commons.Music.Midi;
using static RolandS1Editor.S1ParameterType;
using System.Text.Json;

namespace RolandS1Editor;

// Holds every parameter for one S-1 patch (all 54 CCs).
// Parameters are stored in named groups matching the physical panel sections,
// and also in a flat AllParameters list for easy iteration.
//
// Call ConnectAsync() to open a MIDI port. After that, any change to a
// parameter's Value is automatically sent to the hardware — no extra steps needed.
public class S1Patch : IDisposable
{
    // ── Section groups ──────────────────────────────────────────────────────

    public IReadOnlyList<S1Parameter> Controls   { get; }
    public IReadOnlyList<S1Parameter> Lfo        { get; }
    public IReadOnlyList<S1Parameter> Voice      { get; }
    public IReadOnlyList<S1Parameter> Oscillator { get; }
    public IReadOnlyList<S1Parameter> Filter     { get; }
    public IReadOnlyList<S1Parameter> Envelope   { get; }
    public IReadOnlyList<S1Parameter> Effects    { get; }

    // Flat list of all 54 parameters — handy for sending, saving, loading.
    public IReadOnlyList<S1Parameter> AllParameters { get; }

    // ── Constructor ─────────────────────────────────────────────────────────

    public S1Patch()
    {
        Controls = new List<S1Parameter>
        {
            new("Mod Wheel",        cc:  1, S1Section.Controls),
            new("Expression Pedal", cc: 11, S1Section.Controls),
            new("Damper Pedal",     cc: 64, S1Section.Controls, initialValue: 0, Toggle),
        };

        Lfo = new List<S1Parameter>
        {
            new("LFO Rate",            cc:   3, S1Section.Lfo),
            new("LFO Waveform",        cc:  12, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Triangle", "Sine", "Sawtooth", "Square", "Sample & Hold" }),
            new("LFO Modulation Depth",cc:  17, S1Section.Lfo),
            new("LFO Mode",            cc:  79, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Normal", "Fast" }),
            new("LFO Key Trigger",     cc: 105, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Off", "On" }),
            new("LFO Sync",            cc: 106, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Off", "On" }),
        };

        Voice = new List<S1Parameter>
        {
            new("Portamento Time",        cc:  5, S1Section.Voice),
            new("Pan",                    cc: 10, S1Section.Voice, initialValue: 64),
            new("Portamento Mode",        cc: 31, S1Section.Voice, initialValue: 0, Dropdown,
                new[] { "Off", "Auto", "On" }),
            new("Portamento",             cc: 65, S1Section.Voice, initialValue: 0,  Toggle),
            new("Keyboard Transpose",     cc: 77, S1Section.Voice, initialValue: 64),
            new("Polyphony Mode",         cc: 80, S1Section.Voice, initialValue: 0, Dropdown,
                new[] { "Mono", "Unison", "Poly", "Chord" }),
            new("Chord Voice 2 On/Off",   cc: 81, S1Section.Voice, initialValue: 0,  Toggle),
            new("Chord Voice 3 On/Off",   cc: 82, S1Section.Voice, initialValue: 0,  Toggle),
            new("Chord Voice 4 On/Off",   cc: 83, S1Section.Voice, initialValue: 0,  Toggle),
            new("Chord Voice 2 Key Shift", cc: 85, S1Section.Voice, initialValue: 64, BipolarSlider),
            new("Chord Voice 3 Key Shift", cc: 86, S1Section.Voice, initialValue: 64, BipolarSlider),
            new("Chord Voice 4 Key Shift", cc: 87, S1Section.Voice, initialValue: 64, BipolarSlider),
        };

        Oscillator = new List<S1Parameter>
        {
            new("OSC LFO Pitch",      cc:  13, S1Section.Oscillator),
            new("OSC Range",          cc:  14, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "64'", "32'", "16'", "8'", "4'", "2'" }),
            new("OSC Square PW",      cc:  15, S1Section.Oscillator),
            new("OSC PWM Source",     cc:  16, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "Envelope", "Manual", "LFO" }),
            new("Pitch Bend Sens",    cc:  18, S1Section.Oscillator),
            new("Square Level",       cc:  19, S1Section.Oscillator),
            new("Saw Level",          cc:  20, S1Section.Oscillator),
            new("Sub Level",          cc:  21, S1Section.Oscillator),
            new("Sub Octave Type",    cc:  22, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "-2 Oct Asymmetric", "-2 Oct Symmetric", "-1 Oct Symmetric" }),
            new("Noise Level",        cc:  23, S1Section.Oscillator),
            new("Range Fine Tune",    cc:  76, S1Section.Oscillator, initialValue: 64),
            new("Noise Mode",         cc:  78, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "White", "Pink" }),
            new("Draw Multiply",      cc: 102, S1Section.Oscillator, initialValue: 0),
            new("Chop Overtone",      cc: 103, S1Section.Oscillator),
            new("Chop Comb",          cc: 104, S1Section.Oscillator),
            new("Draw Step/Slope",    cc: 107, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "Off", "On" }),
        };

        Filter = new List<S1Parameter>
        {
            new("Filter Envelope Depth",    cc: 24, S1Section.Filter),
            new("Filter LFO Depth",         cc: 25, S1Section.Filter),
            new("Filter Keytracking",   cc: 26, S1Section.Filter),
            new("Filter Bend Sensitivity",  cc: 27, S1Section.Filter),
            new("Filter Resonance",         cc: 71, S1Section.Filter),
            new("Filter Frequency",         cc: 74, S1Section.Filter),
        };

        Envelope = new List<S1Parameter>
        {
            new("Amp Env Mode",     cc: 28, S1Section.Envelope, initialValue: 0, Dropdown,
                new[] { "Gate", "Envelope" }),
            new("Trigger Mode",     cc: 29, S1Section.Envelope, initialValue: 0, Dropdown,
                new[] { "LFO", "Gate", "Gate+Trig" }),
            new("Sustain",          cc: 30, S1Section.Envelope),
            new("Release",          cc: 72, S1Section.Envelope),
            new("Attack",           cc: 73, S1Section.Envelope),
            new("Decay",            cc: 75, S1Section.Envelope),
        };

        Effects = new List<S1Parameter>
        {
            new("Reverb Time",  cc: 89, S1Section.Effects),
            new("Delay Time",   cc: 90, S1Section.Effects),
            new("Reverb Level", cc: 91, S1Section.Effects),
            new("Delay Level",  cc: 92, S1Section.Effects),
            new("Chorus Type",  cc: 93, S1Section.Effects, initialValue: 0, Dropdown,
                new[] { "Off", "Type 1", "Type 2", "Type 3", "Type 4" }),
        };

        // Build the flat list by concatenating every section in panel order.
        AllParameters = Controls
            .Concat(Lfo)
            .Concat(Voice)
            .Concat(Oscillator)
            .Concat(Filter)
            .Concat(Envelope)
            .Concat(Effects)
            .ToList();
    }

    // ── MIDI connection ──────────────────────────────────────────────────────

    private IMidiOutput? _output;
    private byte _statusByte;   // pre-computed from the channel; changes only on reconnect

    public bool IsConnected => _output != null;

    // Opens the specified MIDI output port and starts auto-sending CC messages
    // whenever any parameter value changes.
    //
    // access  – MidiAccessManager.Default (passed in so S1Patch stays testable)
    // portId  – IMidiPortDetails.Id of the chosen output port
    // channel – MIDI channel 1–16; Roland S-1 defaults to channel 3
#pragma warning disable CS0618  // IMidiAccess is obsolete but IMidiAccess2 is not implemented by WinMM
    public async Task ConnectAsync(IMidiAccess access, string portId, int channel = 3)
#pragma warning restore CS0618
    {
        Disconnect();   // close any previous connection first

        _output     = await access.OpenOutputAsync(portId);
        _statusByte = (byte)(0xB0 | (channel - 1));

        // Wire every parameter so that changing its Value calls SendOne automatically.
        foreach (var param in AllParameters)
            param._onSend = SendOne;
    }

    // Closes the MIDI output port and unwires the auto-send callbacks.
    public void Disconnect()
    {
        if (_output == null) return;

        foreach (var param in AllParameters)
            param._onSend = null;

        _output.Dispose();
        _output = null;
    }

    // Called by MainWindow when a CC arrives from the hardware.
    // Routes it to the right parameter without echoing back to the output.
    public void HandleIncomingCC(int ccNumber, int value) =>
        GetByCC(ccNumber)?.UpdateFromMidi(value);

    // Sends a single CC message for one parameter. Called automatically on value change.
    private void SendOne(S1Parameter param)
    {
        // If somehow called while disconnected, do nothing.
        if (_output == null) return;

        _output.Send(
            new[] { _statusByte, (byte)param.CcNumber, (byte)param.Value },
            offset: 0, length: 3, timestamp: 0);
    }

    // ── Preset save / load ───────────────────────────────────────────────────

    // Snapshot all 54 current values into a serialisable preset object.
    public S1PresetFile ToPreset(string name) => new()
    {
        Name       = name,
        Parameters = AllParameters
            .Select(p => new ParameterEntry { Name = p.Name, Cc = p.CcNumber, Value = p.Value })
            .ToList(),
    };

    // Apply a loaded preset to the model and notify the UI.
    // Uses UpdateFromMidi so values are not immediately echoed back to the hardware
    // — the caller should follow up with SendAllAsync() to sync the hardware.
    public void LoadPreset(S1PresetFile preset)
    {
        foreach (var entry in preset.Parameters)
            GetByCC(entry.Cc)?.UpdateFromMidi(entry.Value);
    }

    // ── Lookup helpers ───────────────────────────────────────────────────────

    // Find a parameter by its CC number (returns null if not found).
    public S1Parameter? GetByCC(int ccNumber) =>
        AllParameters.FirstOrDefault(p => p.CcNumber == ccNumber);

    // ── Bulk send ────────────────────────────────────────────────────────────

    // Sends every parameter's current value to the hardware in one go.
    // Useful after loading a saved patch to sync the hardware to the editor state.
    // A small delay between messages avoids overwhelming the S-1's MIDI buffer.
    // CCs that are never bulk-sent to hardware — they are physical controllers
    // whose position on the device should not be overridden by the editor.
    private static readonly HashSet<int> _noBulkSend = new() { 1, 11 };

    public async Task SendAllAsync()
    {
        if (_output == null) return;

        foreach (var param in AllParameters)
        {
            if (_noBulkSend.Contains(param.CcNumber)) continue;
            SendOne(param);
            await Task.Delay(5);
        }
    }

    // Sends a MIDI Program Change on the connected channel.
    // The S-1 maps its 64 patterns as programs 0–63 (group × 16 + pattern_index).
    public void SendProgramChange(int program)
    {
        if (_output == null) return;
        byte ch = (byte)(_statusByte & 0x0F);
        _output.Send(new[] { (byte)(0xC0 | ch), (byte)Math.Clamp(program, 0, 127) },
            offset: 0, length: 2, timestamp: 0);
    }

    public void Dispose() => Disconnect();
}
