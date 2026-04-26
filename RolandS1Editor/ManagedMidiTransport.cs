using System;
using Commons.Music.Midi;

namespace RolandS1Editor;

// IS1MidiTransport implementation for the standalone desktop app.
// Wraps a Commons.Music.Midi IMidiOutput opened by the app's connect logic.
public sealed class ManagedMidiTransport : IS1MidiTransport, IDisposable
{
    private readonly IMidiOutput _output;

    public ManagedMidiTransport(IMidiOutput output) => _output = output;

    public void SendCC(int channel, int ccNumber, int value) =>
        _output.Send(
            new byte[] { (byte)(0xB0 | (channel - 1)), (byte)ccNumber, (byte)value },
            offset: 0, length: 3, timestamp: 0);

    public void SendProgramChange(int channel, int program) =>
        _output.Send(
            new byte[] { (byte)(0xC0 | (channel - 1)), (byte)program },
            offset: 0, length: 2, timestamp: 0);

    public void Dispose() => _output.Dispose();
}
