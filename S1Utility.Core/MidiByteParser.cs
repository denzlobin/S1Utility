using System;

namespace S1Utility.Core;

// Parses raw MIDI bytes into S1MidiEvent. Stateless across calls — a buffer
// is expected to be a self-contained chunk (running status within a chunk is
// honored; running status across chunks is not, since managed-midi and most
// raw-byte backends deliver each MessageReceived as a fresh chunk).
//
// Only the event kinds S1Utility cares about are emitted: CC, NoteOn, NoteOff,
// ProgramChange. Channel pressure, poly aftertouch, pitch bend, sysex, and
// system realtime/common are recognized and stepped over so the parser doesn't
// desync on a mixed-traffic buffer.
public static class MidiByteParser
{
    public static void Parse(ReadOnlySpan<byte> data, Action<S1MidiEvent> emit)
    {
        if (emit == null) throw new ArgumentNullException(nameof(emit));

        int i = 0;
        byte runningStatus = 0;

        while (i < data.Length)
        {
            byte first = data[i];
            byte status;

            if ((first & 0x80) != 0)
            {
                status = first;
                i++;
                // System common cancels running status; system realtime does not.
                if (status is >= 0xF0 and < 0xF8)
                    runningStatus = 0;
                else if (status < 0xF0)
                    runningStatus = status;
                // else: system realtime (F8..FF) leaves runningStatus alone.
            }
            else
            {
                // Data byte where a status was expected — reuse the last channel-voice status.
                if (runningStatus == 0) return; // malformed: data without prior status
                status = runningStatus;
            }

            int statusType = status & 0xF0;
            int channel    = status & 0x0F;

            switch (statusType)
            {
                case 0x80: // NoteOff: note, velocity
                    if (i + 1 >= data.Length) return;
                    emit(S1MidiEvent.NoteOff(channel, data[i]));
                    i += 2;
                    break;

                case 0x90: // NoteOn: note, velocity (vel=0 means NoteOff)
                    if (i + 1 >= data.Length) return;
                    {
                        byte note = data[i];
                        byte vel  = data[i + 1];
                        emit(vel > 0
                            ? S1MidiEvent.NoteOn(channel, note, vel)
                            : S1MidiEvent.NoteOff(channel, note));
                    }
                    i += 2;
                    break;

                case 0xA0: // Poly Aftertouch: note, pressure — skip
                    if (i + 1 >= data.Length) return;
                    i += 2;
                    break;

                case 0xB0: // CC: number, value
                    if (i + 1 >= data.Length) return;
                    emit(S1MidiEvent.CC(channel, data[i], data[i + 1]));
                    i += 2;
                    break;

                case 0xC0: // Program Change: program
                    if (i >= data.Length) return;
                    emit(S1MidiEvent.ProgramChange(channel, data[i]));
                    i += 1;
                    break;

                case 0xD0: // Channel Aftertouch: pressure — skip
                    if (i >= data.Length) return;
                    i += 1;
                    break;

                case 0xE0: // Pitch Bend: lsb, msb — skip
                    if (i + 1 >= data.Length) return;
                    i += 2;
                    break;

                case 0xF0: // System messages
                    switch (status)
                    {
                        case 0xF0: // Sysex start — consume bytes until 0xF7 (or buffer end)
                            while (i < data.Length && data[i] != 0xF7) i++;
                            if (i < data.Length) i++; // skip the terminating F7
                            break;
                        case 0xF1: // MTC quarter frame: 1 data byte
                        case 0xF3: // Song Select: 1 data byte
                            if (i >= data.Length) return;
                            i += 1;
                            break;
                        case 0xF2: // Song Position Pointer: 2 data bytes
                            if (i + 1 >= data.Length) return;
                            i += 2;
                            break;
                        // F4, F5 (undefined), F6 (Tune Request), F8..FF (System Realtime):
                        // all carry zero data bytes — nothing further to consume.
                    }
                    break;

                default:
                    // Should be unreachable given the high-bit masking above.
                    return;
            }
        }
    }
}
