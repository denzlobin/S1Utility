namespace RolandS1Editor;

// Abstracts MIDI output so S1Patch is independent of the transport mechanism.
// The standalone app implements this via managed-midi; the VST plugin via host MIDI output.
public interface IS1MidiTransport
{
    void SendCC(int channel, int ccNumber, int value);
    void SendProgramChange(int channel, int program);
}
