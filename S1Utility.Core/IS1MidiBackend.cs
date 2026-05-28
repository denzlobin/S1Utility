using System.Collections.Generic;

namespace S1Utility.Core;

// Abstracts the platform-specific MIDI subsystem (WinMM, CoreMIDI, ALSA, ...).
// Enumeration and port opening live here; the rest of the app sees only the
// IS1MidiTransport and IS1MidiInput handles this returns.
public interface IS1MidiBackend
{
    // Re-scans the OS for current devices. Implementations should be cheap to
    // call repeatedly; callers re-enumerate on dropdown open and reconnect.
    void Refresh();

    IReadOnlyList<string> OutputNames { get; }
    IReadOnlyList<string> InputNames  { get; }

    // Opens the output port by name. Throws if the port can't be opened.
    // The returned transport is owned by the caller and must be disposed.
    IS1MidiTransport OpenOutput(string name);

    // Opens the input port by name. The returned input is not yet listening;
    // caller subscribes to EventReceived then calls Start().
    IS1MidiInput OpenInput(string name);
}
