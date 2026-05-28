namespace S1Utility.Core;

// Backend-neutral MIDI event. Lets IS1MidiInput consumers handle incoming
// traffic without taking a dependency on any specific MIDI library's event
// types. Channel is 0-based (0..15) to match the wire format; the rest of
// the app converts to 1-based at the UI seam.
public enum S1MidiEventKind
{
    ControlChange,
    NoteOn,
    NoteOff,
    ProgramChange,
}

public readonly record struct S1MidiEvent(
    S1MidiEventKind Kind,
    int Channel,
    int Data1,
    int Data2)
{
    public static S1MidiEvent CC(int channel, int cc, int value) =>
        new(S1MidiEventKind.ControlChange, channel, cc, value);

    public static S1MidiEvent NoteOn(int channel, int note, int velocity) =>
        new(S1MidiEventKind.NoteOn, channel, note, velocity);

    public static S1MidiEvent NoteOff(int channel, int note) =>
        new(S1MidiEventKind.NoteOff, channel, note, 0);

    public static S1MidiEvent ProgramChange(int channel, int program) =>
        new(S1MidiEventKind.ProgramChange, channel, program, 0);
}
