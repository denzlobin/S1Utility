using System;
using System.Threading;
using Commons.Music.Midi;

namespace S1Utility;

// IS1MidiTransport implementation for the standalone desktop app.
// Wraps a Commons.Music.Midi IMidiOutput opened by the app's connect logic.
public sealed class ManagedMidiTransport : IS1MidiTransport, IDisposable
{
    private readonly IMidiOutput _output;
    private readonly Action?     _onDisconnect;
    private int                  _notified;

    public ManagedMidiTransport(IMidiOutput output, Action? onDisconnect = null)
    {
        _output       = output;
        _onDisconnect = onDisconnect;
    }

    public void SendCC(int channel, int ccNumber, int value)
    {
        if (channel < 1 || channel > 16)    throw new ArgumentOutOfRangeException(nameof(channel),  channel,  "MIDI channel must be 1–16");
        if (ccNumber < 0 || ccNumber > 127) throw new ArgumentOutOfRangeException(nameof(ccNumber), ccNumber, "CC number must be 0–127");
        if (value    < 0 || value    > 127) throw new ArgumentOutOfRangeException(nameof(value),    value,    "CC value must be 0–127");
        try { _output.Send(new byte[] { (byte)(0xB0 | (channel - 1)), (byte)ccNumber, (byte)value }, 0, 3, 0); }
        catch { NotifyDisconnect(); }
    }

    public void SendProgramChange(int channel, int program)
    {
        if (channel < 1 || channel > 16) throw new ArgumentOutOfRangeException(nameof(channel), channel, "MIDI channel must be 1–16");
        if (program < 0 || program > 127) throw new ArgumentOutOfRangeException(nameof(program), program, "Program number must be 0–127");
        try { _output.Send(new byte[] { (byte)(0xC0 | (channel - 1)), (byte)program }, 0, 2, 0); }
        catch { NotifyDisconnect(); }
    }

    private void NotifyDisconnect()
    {
        if (Interlocked.Exchange(ref _notified, 1) == 0)
            _onDisconnect?.Invoke();
    }

    public void Dispose() => _output.Dispose();
}
