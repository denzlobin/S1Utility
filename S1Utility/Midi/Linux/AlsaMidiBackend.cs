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
    // managed-midi marks IMidiAccess obsolete in favour of IMidiAccess2 (the two
    // will converge in a future API-breaking release). On Linux, Default is
    // AlsaMidiAccess, which implements IMidiAccess2, so the cast is safe. Every
    // member used below (Outputs/Inputs/OpenOutputAsync/OpenInputAsync) is
    // inherited from the base interface.
    // Null when the ALSA sequencer is unavailable (no /dev/snd/seq, e.g.
    // snd-seq not loaded, containers, headless CI). The app then runs with no
    // ports instead of crashing. The failed static init is cached by .NET, so
    // a restart is needed once ALSA becomes available.
    private readonly IMidiAccess2? _access = TryGetAccess();

    private static IMidiAccess2? TryGetAccess()
    {
        try { return (IMidiAccess2)MidiAccessManager.Default; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ALSA MIDI unavailable: {ex.GetBaseException().Message}");
            return null;
        }
    }

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
        if (_access == null) return;
        _outputs     = _access.Outputs.ToList();
        _inputs      = _access.Inputs.ToList();
        _outputNames = _outputs.Select(p => p.Name).ToList();
        _inputNames  = _inputs.Select(p => p.Name).ToList();
    }

    public IS1MidiTransport OpenOutput(string name)
    {
        var port = _outputs.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"MIDI output port not found: '{name}'");
        var output = _access!.OpenOutputAsync(port.Id).GetAwaiter().GetResult();
        return new AlsaMidiTransport(output, name);
    }

    public IS1MidiInput OpenInput(string name)
    {
        var port = _inputs.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"MIDI input port not found: '{name}'");
        var input = _access!.OpenInputAsync(port.Id).GetAwaiter().GetResult();
        return new AlsaMidiInput(input, name);
    }
}
