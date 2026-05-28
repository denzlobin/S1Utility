using System;
using System.Collections.Generic;
using System.Linq;
using Commons.Music.Midi;
using S1Utility.Core;

namespace S1Utility.Midi.Linux;

// IS1MidiBackend implementation backed by managed-midi. Targets Linux/ALSA;
// managed-midi also supports CoreMIDI/WinMM but the host only instantiates
// this backend on Linux.
public sealed class AlsaMidiBackend : IS1MidiBackend
{
    private readonly IMidiAccess _access = MidiAccessManager.Default;

    // Cached port snapshots. We hold IMidiPortDetails (not just names) because
    // open-by-id is the supported path in managed-midi — looking up a port by
    // name across Refresh() boundaries would risk grabbing the wrong handle if
    // two ports shared a name.
    private List<IMidiPortDetails> _outputs = new();
    private List<IMidiPortDetails> _inputs  = new();
    private List<string>           _outputNames = new();
    private List<string>           _inputNames  = new();

    public AlsaMidiBackend() => Refresh();

    public IReadOnlyList<string> OutputNames => _outputNames;
    public IReadOnlyList<string> InputNames  => _inputNames;

    public void Refresh()
    {
        _outputs     = _access.Outputs.ToList();
        _inputs      = _access.Inputs.ToList();
        _outputNames = _outputs.Select(p => p.Name).ToList();
        _inputNames  = _inputs.Select(p => p.Name).ToList();
    }

    public IS1MidiTransport OpenOutput(string name)
    {
        var port = _outputs.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"MIDI output port not found: '{name}'");
        var output = _access.OpenOutputAsync(port.Id).GetAwaiter().GetResult();
        return new AlsaMidiTransport(output, name);
    }

    public IS1MidiInput OpenInput(string name)
    {
        var port = _inputs.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"MIDI input port not found: '{name}'");
        var input = _access.OpenInputAsync(port.Id).GetAwaiter().GetResult();
        return new AlsaMidiInput(input, name);
    }
}
