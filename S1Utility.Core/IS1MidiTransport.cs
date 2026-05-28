namespace S1Utility.Core;

// Abstracts MIDI output so S1Patch is independent of the transport mechanism.
// Implementations come from an IS1MidiBackend (DryWetMidi on Windows/macOS,
// managed-midi on Linux). A future VST plugin host would supply its own.
public interface IS1MidiTransport
{
    void SendCC(int channel, int ccNumber, int value);
    void SendProgramChange(int channel, int program);
}
