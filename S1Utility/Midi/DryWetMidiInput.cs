using System;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using S1Utility.Core;

namespace S1Utility.Midi;

// IS1MidiInput implementation backed by a DryWetMidi InputDevice.
// Normalizes DryWetMidi event types into the backend-neutral S1MidiEvent
// so the rest of the app sees no DryWetMidi types.
internal sealed class DryWetMidiInput : IS1MidiInput
{
    private readonly InputDevice _device;
    private bool _listening;

    public DryWetMidiInput(InputDevice device)
    {
        _device = device;
        _device.EventReceived += OnEventReceived;
    }

    public string Name => _device.Name;

    public event EventHandler<S1MidiEvent>? EventReceived;

    public void Start()
    {
        if (_listening) return;
        _device.StartEventsListening();
        _listening = true;
    }

    public void Stop()
    {
        if (!_listening) return;
        try { _device.StopEventsListening(); }
        catch { /* tolerate already-stopped */ }
        _listening = false;
    }

    private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        var handler = EventReceived;
        if (handler == null) return;

        switch (e.Event)
        {
            case ControlChangeEvent cc:
                handler(this, S1MidiEvent.CC(
                    (int)cc.Channel, (int)cc.ControlNumber, (int)cc.ControlValue));
                break;
            case NoteOnEvent noteOn:
                if (noteOn.Velocity > 0)
                    handler(this, S1MidiEvent.NoteOn(
                        (int)noteOn.Channel, (int)noteOn.NoteNumber, (int)noteOn.Velocity));
                else
                    handler(this, S1MidiEvent.NoteOff(
                        (int)noteOn.Channel, (int)noteOn.NoteNumber));
                break;
            case NoteOffEvent noteOff:
                handler(this, S1MidiEvent.NoteOff(
                    (int)noteOff.Channel, (int)noteOff.NoteNumber));
                break;
            case ProgramChangeEvent pc:
                handler(this, S1MidiEvent.ProgramChange(
                    (int)pc.Channel, (int)pc.ProgramNumber));
                break;
        }
    }

    public void Dispose()
    {
        Stop();
        _device.EventReceived -= OnEventReceived;
        _device.Dispose();
    }
}
