using System;
using System.Threading;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using S1Utility.Core;

namespace S1Utility;

// IS1MidiTransport implementation for the standalone desktop app.
// Wraps a DryWetMidi OutputDevice opened by the app's connect logic.
public sealed class DryWetMidiTransport : IS1MidiTransport, IDisposable
{
    private readonly OutputDevice _output;
    private readonly string       _portName;
    private readonly Action?      _onDisconnect;
    private int                   _notified;

    public DryWetMidiTransport(OutputDevice output, string portName, Action? onDisconnect = null)
    {
        _output       = output;
        _portName     = portName;
        _onDisconnect = onDisconnect;
    }

    public void SendCC(int channel, int ccNumber, int value)
    {
        if (channel < 1 || channel > 16)    throw new ArgumentOutOfRangeException(nameof(channel),  channel,  "MIDI channel must be 1–16");
        if (ccNumber < 0 || ccNumber > 127) throw new ArgumentOutOfRangeException(nameof(ccNumber), ccNumber, "CC number must be 0–127");
        if (value    < 0 || value    > 127) throw new ArgumentOutOfRangeException(nameof(value),    value,    "CC value must be 0–127");
        try
        {
            var evt = new ControlChangeEvent((SevenBitNumber)ccNumber, (SevenBitNumber)value)
            {
                Channel = (FourBitNumber)(channel - 1)
            };
            _output.SendEvent(evt);
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"MIDI send CC{ccNumber}={value} on ch{channel} failed (port '{_portName}')", ex);
            NotifyDisconnect();
        }
    }

    public void SendProgramChange(int channel, int program)
    {
        if (channel < 1 || channel > 16) throw new ArgumentOutOfRangeException(nameof(channel), channel, "MIDI channel must be 1–16");
        if (program < 0 || program > 127) throw new ArgumentOutOfRangeException(nameof(program), program, "Program number must be 0–127");
        try
        {
            var evt = new ProgramChangeEvent((SevenBitNumber)program)
            {
                Channel = (FourBitNumber)(channel - 1)
            };
            _output.SendEvent(evt);
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"MIDI send PC={program} on ch{channel} failed (port '{_portName}')", ex);
            NotifyDisconnect();
        }
    }

    private void NotifyDisconnect()
    {
        if (Interlocked.Exchange(ref _notified, 1) == 0)
            _onDisconnect?.Invoke();
    }

    public void Dispose() => _output.Dispose();
}
