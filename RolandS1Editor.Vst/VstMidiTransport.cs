using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RolandS1Editor.Vst.Vst3;

namespace RolandS1Editor.Vst;

// IS1MidiTransport for the VST3 plugin.
// SendCC / SendProgramChange enqueue events; FlushToEventList drains them
// into the output IEventList during IAudioProcessor.Process().
public sealed unsafe class VstMidiTransport : IS1MidiTransport
{
    private readonly ConcurrentQueue<Vst3Event> _queue = new();

    public void SendCC(int channel, int ccNumber, int value)
    {
        _queue.Enqueue(new Vst3Event
        {
            busIndex     = 0,
            sampleOffset = 0,
            ppqPosition  = 0,
            flags        = 0,
            type         = Vst3Event.kLegacyMidiCCOut,
            midiCCOut    = new LegacyMidiCCOut
            {
                controlNumber = (byte)ccNumber,
                value         = (sbyte)Math.Clamp(value, -128, 127),
                value2        = 0,
                reserved      = 0,
            },
        });
    }

    public void SendProgramChange(int channel, int program)
    {
        // VST3 has no direct ProgramChange event type; use a MIDI CC-style raw message
        // via kLegacyMidiCCOut with controlNumber 0x7F (non-standard) OR handle via
        // IUnitInfo / program lists.  For now encode as a 0xC0 legacy event.
        // TODO: implement properly once IUnitInfo is wired.
    }

    // Called from PluginObject.Process() each audio block.
    // outputEvents is the IEventList* provided by the DAW in ProcessData.
    internal void FlushToEventList(void* outputEvents)
    {
        if (outputEvents == null || _queue.IsEmpty) return;

        // Call IEventList::AddEvent (vtable slot 5) for each queued event.
        // IEventList vtable: [0]QI [1]AddRef [2]Release [3]GetEventCount [4]GetEvent [5]AddEvent
        var vtbl     = *(void***)outputEvents;
        var addEvent = (delegate* unmanaged<void*, Vst3Event*, int>)vtbl[5];

        while (_queue.TryDequeue(out var ev))
        {
            var local = ev; // copy to stack so we can take a pointer
            addEvent(outputEvents, &local);
        }
    }
}
