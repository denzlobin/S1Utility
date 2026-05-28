using System;

namespace S1Utility.Core;

// Abstracts a single open MIDI input port. Implementations raise EventReceived
// from a background thread; callers must marshal to the UI thread themselves.
public interface IS1MidiInput : IDisposable
{
    string Name { get; }

    // Fired for every incoming event after Start() and before Stop().
    // Thread context: backend-defined (typically a worker thread).
    event EventHandler<S1MidiEvent>? EventReceived;

    void Start();
    void Stop();
}
