using System;
using System.Collections.Generic;
using System.Linq;
using Melanchall.DryWetMidi.Multimedia;
using S1Utility.Core;

namespace S1Utility.Midi;

// IS1MidiBackend implementation backed by Melanchall.DryWetMidi.Multimedia.
// Supports Windows (WinMM) and macOS (CoreMIDI). Linux throws at runtime
// inside DryWetMidi; the Linux backend lands in Phase 2.
public sealed class DryWetMidiBackend : IS1MidiBackend
{
    private List<string> _outputNames = new();
    private List<string> _inputNames  = new();

    public DryWetMidiBackend() => Refresh();

    public IReadOnlyList<string> OutputNames => _outputNames;
    public IReadOnlyList<string> InputNames  => _inputNames;

    public void Refresh()
    {
        // OutputDevice.GetAll() returns live instances we don't intend to hold.
        // Snapshot the names and dispose right away; we re-open by name on connect.
        var outs = OutputDevice.GetAll().ToList();
        try { _outputNames = outs.Select(d => d.Name).ToList(); }
        finally { foreach (var d in outs) d.Dispose(); }

        // Input enumeration follows the same dispose-after-snapshot pattern so
        // the backend doesn't hold device handles between Refresh() calls.
        var ins = InputDevice.GetAll().ToList();
        try { _inputNames = ins.Select(d => d.Name).ToList(); }
        finally { foreach (var d in ins) d.Dispose(); }
    }

    public IS1MidiTransport OpenOutput(string name)
    {
        var output = OutputDevice.GetByName(name);
        // Open the port explicitly so failures surface here, not on first SendEvent.
        output.PrepareForEventsSending();
        return new DryWetMidiTransport(output, name);
    }

    public IS1MidiInput OpenInput(string name)
    {
        var input = InputDevice.GetByName(name);
        return new DryWetMidiInput(input);
    }
}
