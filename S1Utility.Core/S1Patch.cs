using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static S1Utility.Core.S1ParameterType;

namespace S1Utility.Core;

// Holds every parameter for one S-1 patch (all 54 CCs).
// Parameters are stored in named groups matching the physical panel sections,
// and also in a flat AllParameters list for easy iteration.
//
// Call SetTransport() to wire a MIDI output. After that, any change to a
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

    private readonly Dictionary<int, S1Parameter> _byCC;

    // ── Constructor ─────────────────────────────────────────────────────────

    public S1Patch()
    {
        Controls = new List<S1Parameter>
        {
            new("Mod Wheel",        cc:  1, S1Section.Controls),
            new("Exp Pedal", cc: 11, S1Section.Controls),
            new("Damper Pedal",     cc: 64, S1Section.Controls, initialValue: 0, Toggle),
        };

        Lfo = new List<S1Parameter>
        {
            new("Rate",            cc:   3, S1Section.Lfo),
            new("Waveform",        cc:  12, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Sawtooth", "Inv Saw", "Triangle", "Square", "Random", "Noise" }),
            new("Mod Depth",cc:  17, S1Section.Lfo),
            new("Mode",            cc:  79, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Normal", "Fast" }),
            new("Key Trigger",     cc: 105, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Off", "On" }),
            new("Sync",            cc: 106, S1Section.Lfo, initialValue: 0, Dropdown,
                new[] { "Off", "On" }),
        };

        Voice = new List<S1Parameter>
        {
            new("Glide Time",        cc:  5, S1Section.Voice),
            new("Pan",                    cc: 10, S1Section.Voice, initialValue: 64),
            new("Portamento",             cc: 31, S1Section.Voice, initialValue: 0, Dropdown,
                new[] { "Off", "Auto", "On" }),
            new("Transpose",     cc: 77, S1Section.Voice, initialValue: 64),
            new("Polyphony",              cc: 80, S1Section.Voice, initialValue: 0, Dropdown,
                new[] { "Mono", "Unison", "Poly", "Chord" }),
            new("Ch V2 On/Off",   cc: 81, S1Section.Voice, initialValue: 0,  Toggle),
            new("Ch V3 On/Off",   cc: 82, S1Section.Voice, initialValue: 0,  Toggle),
            new("Ch V4 On/Off",   cc: 83, S1Section.Voice, initialValue: 0,  Toggle),
            new("V2 Key Shift", cc: 85, S1Section.Voice, initialValue: 64, BipolarSlider),
            new("V3 Key Shift", cc: 86, S1Section.Voice, initialValue: 64, BipolarSlider),
            new("V4 Key Shift", cc: 87, S1Section.Voice, initialValue: 64, BipolarSlider),
        };

        Oscillator = new List<S1Parameter>
        {
            new("LFO Pitch",      cc:  13, S1Section.Oscillator),
            new("Range",          cc:  14, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "64'", "32'", "16'", "8'", "4'", "2'" }),
            new("Square PW",      cc:  15, S1Section.Oscillator),
            new("PWM Source",     cc:  16, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "Envelope", "Manual", "LFO" }),
            new("Bend Amount",    cc:  18, S1Section.Oscillator),
            new("Square",       cc:  19, S1Section.Oscillator),
            new("Saw",          cc:  20, S1Section.Oscillator),
            new("Sub",          cc:  21, S1Section.Oscillator),
            new("Sub Oct Type",    cc:  22, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "-2 Oct Asym", "-2 Oct", "-1 Oct" }),
            new("Noise",        cc:  23, S1Section.Oscillator),
            new("Fine Tune",    cc:  76, S1Section.Oscillator, initialValue: 64),
            new("Noise Mode",         cc:  78, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "Pink", "White" }),
            new("Draw Multiply",      cc: 102, S1Section.Oscillator, initialValue: 0),
            new("Chop Overtone",      cc: 103, S1Section.Oscillator),
            new("Chop Comb",          cc: 104, S1Section.Oscillator),
            new("Draw",    cc: 107, S1Section.Oscillator, initialValue: 0, Dropdown,
                new[] { "Off", "Step", "Slope" }),
        };

        Filter = new List<S1Parameter>
        {
            new("Env Amount",    cc: 24, S1Section.Filter),
            new("LFO Amount",         cc: 25, S1Section.Filter),
            new("Keytracking",   cc: 26, S1Section.Filter),
            new("Bend Amount",  cc: 27, S1Section.Filter),
            new("Resonance",         cc: 71, S1Section.Filter),
            new("Cutoff",         cc: 74, S1Section.Filter),
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
            new("Time",  cc: 89, S1Section.Effects),
            new("Time",   cc: 90, S1Section.Effects),
            new("Level", cc: 91, S1Section.Effects),
            new("Level",  cc: 92, S1Section.Effects),
            new("Type",  cc: 93, S1Section.Effects, initialValue: 0, Dropdown,
                new[] { "Off", "Type 1", "Type 2", "Type 3", "Type 4" }),
        };

        AllParameters = Controls
            .Concat(Lfo)
            .Concat(Voice)
            .Concat(Oscillator)
            .Concat(Filter)
            .Concat(Envelope)
            .Concat(Effects)
            .ToList();

        _byCC = AllParameters.ToDictionary(p => p.CcNumber);

        _unsyncedCount = AllParameters.Count(p => !_noBulkSend.Contains(p.CcNumber));
        foreach (var p in AllParameters)
            p.SyncStateChanged += OnParameterSyncChanged;
    }

    // ── Sync state ───────────────────────────────────────────────────────────

    private int _unsyncedCount;

    // Number of parameters whose synth value is not yet confirmed to match the editor.
    public int UnsyncedCount => _unsyncedCount;

    // The currently-unsynced tracked parameters (excludes the live-controller set
    // for the same reason UnsyncedCount does). Order matches AllParameters.
    public IEnumerable<S1Parameter> UnsyncedParameters =>
        AllParameters.Where(p => !p.IsSynced && !_noBulkSend.Contains(p.CcNumber));

    // Fires (from any thread) whenever UnsyncedCount changes.
    public event EventHandler<int>? SyncCountChanged;

    // Mark one parameter synced by CC number. No-op if not found or already synced.
    public void MarkSynced(int ccNumber) => GetByCC(ccNumber)?.MarkSynced();

    // Mark all parameters synced (e.g. after Send All or a successful PRM load).
    public void MarkAllSynced()
    {
        foreach (var p in AllParameters)
            p.MarkSynced();
    }

    // Reset all parameters to unsynced — call at the start of every connect/reconnect.
    public void ResetAllSync()
    {
        foreach (var p in AllParameters)
            p.ResetSync();
    }

    private void OnParameterSyncChanged(object? sender, bool synced)
    {
        if (sender is S1Parameter p && _noBulkSend.Contains(p.CcNumber)) return;
        int remaining = synced
            ? Interlocked.Decrement(ref _unsyncedCount)
            : Interlocked.Increment(ref _unsyncedCount);
        SyncCountChanged?.Invoke(this, Math.Max(0, remaining));
    }

    // ── MIDI transport ──────────────────────────────────────────────────────

    private IS1MidiTransport? _transport;
    private int _channel = 3;

    public bool IsConnected => _transport != null;

    // Wires a transport and channel. Disposes the previous transport if it is IDisposable.
    // Pass null to disconnect.
    public void SetTransport(IS1MidiTransport? transport, int channel = 3)
    {
        if (_transport is IDisposable d) d.Dispose();
        _transport = transport;
        _channel   = channel;
        foreach (var param in AllParameters)
            param._onSend = transport != null ? SendOne : null;
    }

    public void Disconnect() => SetTransport(null);

    // ── UI dispatcher ───────────────────────────────────────────────────────

    // Optional marshaller used by HandleIncomingCC so S1Parameter.ValueChanged
    // always fires on the UI thread when MIDI arrives. The host (app startup)
    // wires this to a "run inline if already on UI thread, post otherwise"
    // helper — synchronous-from-UI delivery is required so callers can wrap
    // bulk loads with a transient suppress flag without racing the dispatch.
    // Core itself stays UI-agnostic; if null, HandleIncomingCC runs inline.
    public Action<Action>? UiDispatcher { get; set; }

    // Called by the host when a CC arrives from the hardware.
    // Routes it to the right parameter without echoing back to the output.
    // With UiDispatcher wired, the parameter update (and the ValueChanged event
    // it raises) is marshalled to the UI thread, so subscribers may safely
    // touch UI state without dispatching themselves.
    public void HandleIncomingCC(int ccNumber, int value)
    {
        var param = GetByCC(ccNumber);
        if (param == null) return;
        if (UiDispatcher is { } dispatch)
            dispatch(() => param.UpdateFromMidi(value));
        else
            param.UpdateFromMidi(value);
    }

    private void SendOne(S1Parameter param) =>
        _transport?.SendCC(_channel, param.CcNumber, param.Value);

    // ── Preset save / load ───────────────────────────────────────────────────

    public S1PresetFile ToPreset(string name) => new()
    {
        Name       = name,
        Parameters = AllParameters
            .Select(p => new ParameterEntry { Name = p.Name, Cc = p.CcNumber, Value = p.Value })
            .ToList(),
    };

    public void LoadPreset(S1PresetFile preset)
    {
        foreach (var entry in preset.Parameters)
            GetByCC(entry.Cc)?.UpdateFromMidi(entry.Value);
    }

    // ── Lookup helpers ───────────────────────────────────────────────────────

    public S1Parameter? GetByCC(int ccNumber) =>
        _byCC.TryGetValue(ccNumber, out var p) ? p : null;

    // ── Bulk send ────────────────────────────────────────────────────────────

    // CC64 (HOLD / Damper) is excluded from bulk send so Send All / preset load
    // / Init Patch never override the hardware sustain-pedal state. Same set is
    // excluded from the aggregate UnsyncedCount: bulk send is the editor's only
    // sync mechanism for this CC and the S-1 panel doesn't echo it, so counting
    // it would be permanent noise. Per-parameter IsSynced still tracks honestly
    // for the HOLD widget. Mod Wheel (CC1) and Expression (CC11) are bulk-sent
    // intentionally so Init Patch can reset them to 0 / 127 on the device.
    private static readonly HashSet<int> _noBulkSend = new() { 64 };

    public async Task SendAllAsync()
    {
        if (_transport == null) return;

        foreach (var param in AllParameters)
        {
            if (_noBulkSend.Contains(param.CcNumber)) continue;
            SendOne(param);
            await Task.Delay(5);
        }
    }

    // Sends MIDI panic on the configured channel: All Sound Off (CC 120) silences
    // any ringing voices immediately; All Notes Off (CC 123) clears held notes.
    public void SendPanic()
    {
        if (_transport == null) return;
        _transport.SendCC(_channel, 120, 0);
        _transport.SendCC(_channel, 123, 0);
    }

    // Sends a MIDI Program Change on the connected channel.
    // The S-1 maps its 64 patterns as programs 0–63 (group × 16 + pattern_index).
    public void SendProgramChange(int program) =>
        _transport?.SendProgramChange(_channel, Math.Clamp(program, 0, 127));

    public void SendProgramChange(int program, int channel) =>
        _transport?.SendProgramChange(Math.Clamp(channel, 1, 16), Math.Clamp(program, 0, 127));

    public void Dispose() => Disconnect();
}
