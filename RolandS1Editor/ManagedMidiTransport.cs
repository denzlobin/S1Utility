using System;
using System.Threading;
using Commons.Music.Midi;

namespace RolandS1Editor;

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
        try { _output.Send(new byte[] { (byte)(0xB0 | (channel - 1)), (byte)ccNumber, (byte)value }, 0, 3, 0); }
        catch { NotifyDisconnect(); }
    }

    public void SendProgramChange(int channel, int program)
    {
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
