using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using S1Utility.Core;

namespace S1Utility;

// Owns MIDI device enumeration, connection lifecycle, and incoming event dispatch.
// Backend-agnostic: takes an IS1MidiBackend in the ctor. No Avalonia dependency,
// no DryWetMidi imports — the host picks the backend by platform.
public sealed class MidiManager : IDisposable
{
    private readonly S1Patch         _patch;
    private readonly IS1MidiBackend  _backend;
    private IS1MidiTransport?        _activeOutput;
    private IS1MidiInput?            _activeInput;

    // Updated at connect time; reflects the active receive/send channel.
    public int Channel { get; set; } = 3;

    public IReadOnlyList<string> OutputDeviceNames => _backend.OutputNames;
    public IReadOnlyList<string> InputDeviceNames  => _backend.InputNames;

    // Events fired from the MIDI receive thread — callers must dispatch to UI thread if needed.
    public event EventHandler?      Disconnected;
    public event EventHandler?      NoteOnReceived;
    public event EventHandler?      NoteOffReceived;
    public event EventHandler<int>? ProgramChangeReceived;
    public event EventHandler?      ActivityReceived;

    public MidiManager(S1Patch patch, IS1MidiBackend backend)
    {
        _patch   = patch;
        _backend = backend;
    }

    // Re-enumerates output and input ports via the backend.
    // Stops any active input listener; we do not auto-reconnect.
    public void EnumerateDevices()
    {
        _activeInput?.Stop();
        _activeInput?.Dispose();
        _activeInput = null;
        _backend.Refresh();
    }

    // Opens the output at outIndex, sets the patch transport, then opens the input at inIndex.
    // OutputOk=false means the output port failed to open; caller should abort and show an error.
    // OutputOk=true with InputError non-null means output is open but input failed (show warning, stay connected).
    public Task<(bool OutputOk, string OutPortName, string? InPortName, string? InputError)> ConnectAsync(
        int outIndex, int inIndex, int channel)
    {
        Channel = channel;

        var outPortName = _backend.OutputNames[outIndex];
        IS1MidiTransport output;
        try
        {
            output = _backend.OpenOutput(outPortName);
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"Backend OpenOutput failed for port '{outPortName}'", ex);
            return Task.FromResult<(bool, string, string?, string?)>((false, outPortName, null, ex.Message));
        }

        _activeOutput = output;
        output.Disconnected += OnTransportDisconnected;
        Log.Logger.Info($"MIDI output opened: '{outPortName}' (channel {channel})");
        _patch.SetTransport(output, channel);

        if (inIndex < 0 || inIndex >= _backend.InputNames.Count)
            return Task.FromResult<(bool, string, string?, string?)>((true, outPortName, null, null));

        var inPortName = _backend.InputNames[inIndex];
        try
        {
            _activeInput?.Stop();
            _activeInput?.Dispose();
            _activeInput = _backend.OpenInput(inPortName);
            _activeInput.EventReceived += OnMidiEventReceived;
            _activeInput.Start();
            return Task.FromResult<(bool, string, string?, string?)>((true, outPortName, _activeInput.Name, null));
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"MIDI input listener failed to start on '{inPortName}'", ex);
            if (_activeInput != null)
            {
                _activeInput.EventReceived -= OnMidiEventReceived;
                _activeInput.Dispose();
            }
            _activeInput = null;
            return Task.FromResult<(bool, string, string?, string?)>((true, outPortName, null, ex.Message));
        }
    }

    // User-initiated disconnect. Stops the active input listener so the next
    // Connect starts clean. Does not fire the Disconnected event — the caller
    // already knows.
    public void Disconnect()
    {
        if (_activeInput != null)
        {
            _activeInput.EventReceived -= OnMidiEventReceived;
            _activeInput.Stop();
            _activeInput.Dispose();
            _activeInput = null;
        }
        if (_activeOutput != null)
        {
            _activeOutput.Disconnected -= OnTransportDisconnected;
            _activeOutput = null;
        }
    }

    public int FindOutputIndex(Func<string, bool> predicate)
    {
        for (int i = 0; i < _backend.OutputNames.Count; i++)
            if (predicate(_backend.OutputNames[i])) return i;
        return -1;
    }

    public int FindInputIndex(Func<string, bool> predicate)
    {
        for (int i = 0; i < _backend.InputNames.Count; i++)
            if (predicate(_backend.InputNames[i])) return i;
        return -1;
    }

    private void OnTransportDisconnected(object? sender, EventArgs e) =>
        Disconnected?.Invoke(this, EventArgs.Empty);

    private void OnMidiEventReceived(object? sender, S1MidiEvent e)
    {
        ActivityReceived?.Invoke(this, EventArgs.Empty);
        // Wire channel is 0-based; Channel is 1-based at the UI seam.
        if (e.Channel != Channel - 1) return;

        switch (e.Kind)
        {
            case S1MidiEventKind.ControlChange:
                _patch.MarkSynced(e.Data1);
                _patch.HandleIncomingCC(e.Data1, e.Data2);
                break;
            case S1MidiEventKind.NoteOn:
                NoteOnReceived?.Invoke(this, EventArgs.Empty);
                break;
            case S1MidiEventKind.NoteOff:
                NoteOffReceived?.Invoke(this, EventArgs.Empty);
                break;
            case S1MidiEventKind.ProgramChange:
                ProgramChangeReceived?.Invoke(this, e.Data1);
                break;
        }
    }

    public void Dispose()
    {
        if (_activeInput != null)
        {
            _activeInput.EventReceived -= OnMidiEventReceived;
            _activeInput.Dispose();
            _activeInput = null;
        }
        if (_activeOutput is IDisposable d)
            d.Dispose();
        _activeOutput = null;
    }
}
