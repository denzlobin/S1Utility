using System;
using Commons.Music.Midi;
using S1Utility.Core;

namespace S1Utility.Midi.Linux;

// IS1MidiInput implementation backed by a managed-midi IMidiInput.
// managed-midi delivers MessageReceived with a raw byte buffer; we hand the
// relevant slice to MidiByteParser which emits one S1MidiEvent per recognized
// event. The parser also handles multiple events per buffer (running status,
// system realtime interleaving, etc.).
internal sealed class AlsaMidiInput : IS1MidiInput
{
    private readonly IMidiInput _input;
    private readonly string     _portName;
    private bool                _listening;

    public AlsaMidiInput(IMidiInput input, string portName)
    {
        _input    = input;
        _portName = portName;
    }

    public string Name => _portName;

    public event EventHandler<S1MidiEvent>? EventReceived;

    public void Start()
    {
        if (_listening) return;
        _input.MessageReceived += OnMessageReceived;
        _listening = true;
    }

    public void Stop()
    {
        if (!_listening) return;
        _input.MessageReceived -= OnMessageReceived;
        _listening = false;
    }

    private void OnMessageReceived(object? sender, MidiReceivedEventArgs e)
    {
        var handler = EventReceived;
        if (handler == null) return;

        var slice = new ReadOnlySpan<byte>(e.Data, e.Start, e.Length);
        MidiByteParser.Parse(slice, evt => handler(this, evt));
    }

    public void Dispose()
    {
        Stop();
        try { _input.CloseAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Logger.Error($"MIDI input close failed (port '{_portName}')", ex); }
    }
}
