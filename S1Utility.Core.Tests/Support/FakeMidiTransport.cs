using System;
using System.Collections.Generic;
using S1Utility.Core;

namespace S1Utility.Core.Tests.Support;

// Records every outgoing MIDI message. Used to assert that S1Patch only sends
// the expected CCs (e.g. bulk send excludes CC1/11/64) and that echo prevention
// holds for UpdateFromMidi.
public sealed class FakeMidiTransport : IS1MidiTransport
{
    public List<(int Channel, int Cc, int Value)>      CcMessages       { get; } = new();
    public List<(int Channel, int Program)>            ProgramMessages  { get; } = new();

    public event EventHandler? Disconnected;

    public void SendCC(int channel, int ccNumber, int value) =>
        CcMessages.Add((channel, ccNumber, value));

    public void SendProgramChange(int channel, int program) =>
        ProgramMessages.Add((channel, program));

    // Test helper: fire the disconnect signal as if the underlying port died.
    public void RaiseDisconnected() => Disconnected?.Invoke(this, EventArgs.Empty);
}
