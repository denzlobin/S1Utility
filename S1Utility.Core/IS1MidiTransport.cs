using System;

namespace S1Utility.Core;

// Abstracts MIDI output so S1Patch is independent of the transport mechanism.
// Implementations come from an IS1MidiBackend (DryWetMidi on Windows/macOS,
// managed-midi on Linux). A future VST plugin host would supply its own.
public interface IS1MidiTransport
{
    void SendCC(int channel, int ccNumber, int value);
    void SendProgramChange(int channel, int program);

    // Raised once when the transport detects that the underlying port has
    // gone away (typically by a send failing). Implementations should fire
    // this at most once per transport lifetime; the host treats it as the
    // signal to tear the connection down.
    event EventHandler? Disconnected;
}
