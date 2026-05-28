using System;
using System.Threading;
using Commons.Music.Midi;
using S1Utility.Core;

namespace S1Utility.Midi.Linux;

// IS1MidiTransport implementation backed by a managed-midi IMidiOutput.
// On Linux that's the ALSA sequencer client; the same library also covers
// CoreMIDI/WinMM, but we only wire it on Linux.
internal sealed class AlsaMidiTransport : IS1MidiTransport, IDisposable
{
    private readonly IMidiOutput _output;
    private readonly string      _portName;
    private int                  _notified;

    public AlsaMidiTransport(IMidiOutput output, string portName)
    {
        _output   = output;
        _portName = portName;
    }

    public event EventHandler? Disconnected;

    public void SendCC(int channel, int ccNumber, int value)
    {
        if (channel < 1 || channel > 16)    throw new ArgumentOutOfRangeException(nameof(channel),  channel,  "MIDI channel must be 1–16");
        if (ccNumber < 0 || ccNumber > 127) throw new ArgumentOutOfRangeException(nameof(ccNumber), ccNumber, "CC number must be 0–127");
        if (value    < 0 || value    > 127) throw new ArgumentOutOfRangeException(nameof(value),    value,    "CC value must be 0–127");
        try
        {
            var msg = new byte[]
            {
                (byte)(0xB0 | (channel - 1)),
                (byte)ccNumber,
                (byte)value,
            };
            _output.Send(msg, 0, msg.Length, 0);
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
            var msg = new byte[]
            {
                (byte)(0xC0 | (channel - 1)),
                (byte)program,
            };
            _output.Send(msg, 0, msg.Length, 0);
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
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try { _output.CloseAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Logger.Error($"MIDI output close failed (port '{_portName}')", ex); }
    }
}
