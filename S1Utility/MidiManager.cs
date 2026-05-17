using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using S1Utility.Core;

namespace S1Utility;

// Owns MIDI device enumeration, connection lifecycle, and incoming event dispatch.
// No Avalonia dependency — pure C#, fully unit-testable.
public sealed class MidiManager : IDisposable
{
    private readonly S1Patch _patch;
    private readonly List<InputDevice> _inputDevices = new();
    private InputDevice? _activeInput;

    // Updated at connect time; reflects the active receive/send channel.
    public int Channel { get; set; } = 3;

    public IReadOnlyList<string> OutputDeviceNames { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> InputDeviceNames  { get; private set; } = Array.Empty<string>();

    // Events fired from the MIDI receive thread — callers must dispatch to UI thread if needed.
    public event EventHandler?      Disconnected;
    public event EventHandler?      NoteOnReceived;
    public event EventHandler?      NoteOffReceived;
    public event EventHandler<int>? ProgramChangeReceived;
    public event EventHandler?      ActivityReceived;

    public MidiManager(S1Patch patch)
    {
        _patch = patch;
        EnumerateDevices();
    }

    // Re-enumerates output ports and input devices.
    // Stops any active input listener and clears the device lists.
    public void EnumerateDevices()
    {
        _activeInput?.StopEventsListening();
        _activeInput = null;
        foreach (var d in _inputDevices) d.Dispose();
        _inputDevices.Clear();

        // OutputDevice.GetAll() returns live instances we don't intend to hold —
        // snapshot the names and dispose right away. We re-open by name on connect.
        var outs = OutputDevice.GetAll().ToList();
        try { OutputDeviceNames = outs.Select(d => d.Name).ToList(); }
        finally { foreach (var d in outs) d.Dispose(); }

        _inputDevices.AddRange(InputDevice.GetAll());
        InputDeviceNames = _inputDevices.Select(d => d.Name).ToList();
    }

    // Opens the output at outIndex, sets the patch transport, then opens the input at inIndex.
    // OutputOk=false means the output port failed to open; caller should abort and show an error.
    // OutputOk=true with InputError non-null means output is open but input failed (show warning, stay connected).
    public Task<(bool OutputOk, string OutPortName, string? InPortName, string? InputError)> ConnectAsync(
        int outIndex, int inIndex, int channel)
    {
        Channel = channel;

        var outPortName = OutputDeviceNames[outIndex];
        OutputDevice output;
        try
        {
            output = OutputDevice.GetByName(outPortName);
            // Open the port explicitly so failures surface here rather than on the first SendEvent.
            output.PrepareForEventsSending();
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"OutputDevice.GetByName failed for port '{outPortName}'", ex);
            return Task.FromResult<(bool, string, string?, string?)>((false, outPortName, null, ex.Message));
        }

        var transport = new DryWetMidiTransport(output, outPortName,
            onDisconnect: () => Disconnected?.Invoke(this, EventArgs.Empty));
        Log.Logger.Info($"MIDI output opened: '{outPortName}' (channel {channel})");
        _patch.SetTransport(transport, channel);

        if (inIndex < 0 || inIndex >= _inputDevices.Count)
            return Task.FromResult<(bool, string, string?, string?)>((true, outPortName, null, null));

        try
        {
            _activeInput?.StopEventsListening();
            _activeInput = _inputDevices[inIndex];
            _activeInput.EventReceived += OnMidiEventReceived;
            _activeInput.StartEventsListening();
            return Task.FromResult<(bool, string, string?, string?)>((true, outPortName, _activeInput.Name, null));
        }
        catch (Exception ex)
        {
            var inName = _activeInput?.Name ?? "?";
            Log.Logger.Error($"MIDI input listener failed to start on '{inName}'", ex);
            if (_activeInput != null)
                _activeInput.EventReceived -= OnMidiEventReceived;
            _activeInput = null;
            return Task.FromResult<(bool, string, string?, string?)>((true, outPortName, null, ex.Message));
        }
    }

    public int FindOutputIndex(Func<string, bool> predicate)
    {
        for (int i = 0; i < OutputDeviceNames.Count; i++)
            if (predicate(OutputDeviceNames[i])) return i;
        return -1;
    }

    public int FindInputIndex(Func<string, bool> predicate)
    {
        for (int i = 0; i < _inputDevices.Count; i++)
            if (predicate(_inputDevices[i].Name)) return i;
        return -1;
    }

    private void OnMidiEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        ActivityReceived?.Invoke(this, EventArgs.Empty);
        switch (e.Event)
        {
            case ControlChangeEvent cc when (int)cc.Channel == Channel - 1:
                _patch.MarkSynced((int)cc.ControlNumber);
                _patch.HandleIncomingCC((int)cc.ControlNumber, (int)cc.ControlValue);
                break;
            case NoteOnEvent noteOn when (int)noteOn.Channel == Channel - 1:
                if (noteOn.Velocity > 0) NoteOnReceived?.Invoke(this, EventArgs.Empty);
                else NoteOffReceived?.Invoke(this, EventArgs.Empty);
                break;
            case NoteOffEvent noteOff when (int)noteOff.Channel == Channel - 1:
                NoteOffReceived?.Invoke(this, EventArgs.Empty);
                break;
            case ProgramChangeEvent pc when (int)pc.Channel == Channel - 1:
                ProgramChangeReceived?.Invoke(this, (int)pc.ProgramNumber);
                break;
        }
    }

    public void Dispose()
    {
        _activeInput?.StopEventsListening();
        foreach (var d in _inputDevices) d.Dispose();
    }
}
