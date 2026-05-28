using System.Collections.Generic;
using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

public class MidiByteParserTests
{
    private static List<S1MidiEvent> ParseAll(params byte[] data)
    {
        var sink = new List<S1MidiEvent>();
        MidiByteParser.Parse(data, sink.Add);
        return sink;
    }

    [Fact]
    public void Cc_OnChannel3_EmitsControlChange()
    {
        var events = ParseAll(0xB2, 7, 100);

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.ControlChange, e.Kind);
        Assert.Equal(2,   e.Channel); // 0-based
        Assert.Equal(7,   e.Data1);
        Assert.Equal(100, e.Data2);
    }

    [Fact]
    public void NoteOn_WithVelocity_EmitsNoteOn()
    {
        var events = ParseAll(0x90, 60, 96);

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.NoteOn, e.Kind);
        Assert.Equal(0,  e.Channel);
        Assert.Equal(60, e.Data1);
        Assert.Equal(96, e.Data2);
    }

    [Fact]
    public void NoteOn_WithZeroVelocity_EmitsNoteOff()
    {
        var events = ParseAll(0x90, 60, 0);

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.NoteOff, e.Kind);
        Assert.Equal(60, e.Data1);
    }

    [Fact]
    public void NoteOff_EmitsNoteOff()
    {
        var events = ParseAll(0x82, 64, 0);

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.NoteOff, e.Kind);
        Assert.Equal(2,  e.Channel);
        Assert.Equal(64, e.Data1);
    }

    [Fact]
    public void ProgramChange_EmitsProgramChange()
    {
        var events = ParseAll(0xC0, 5);

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.ProgramChange, e.Kind);
        Assert.Equal(0, e.Channel);
        Assert.Equal(5, e.Data1);
    }

    [Fact]
    public void MultipleEvents_InOneBuffer_AllEmitted()
    {
        var events = ParseAll(
            0xB0, 7, 100,    // CC ch0
            0x90, 60, 96,    // NoteOn ch0
            0xC1, 3);        // PC ch1

        Assert.Equal(3, events.Count);
        Assert.Equal(S1MidiEventKind.ControlChange, events[0].Kind);
        Assert.Equal(S1MidiEventKind.NoteOn,        events[1].Kind);
        Assert.Equal(S1MidiEventKind.ProgramChange, events[2].Kind);
    }

    [Fact]
    public void RunningStatus_ReusesPreviousStatusByte()
    {
        // Three CCs on ch0 sharing one B0 status byte.
        var events = ParseAll(0xB0, 7, 100,  10, 50,  74, 30);

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(S1MidiEventKind.ControlChange, e.Kind));
        Assert.Equal((7,  100), (events[0].Data1, events[0].Data2));
        Assert.Equal((10, 50),  (events[1].Data1, events[1].Data2));
        Assert.Equal((74, 30),  (events[2].Data1, events[2].Data2));
    }

    [Fact]
    public void SystemCommon_CancelsRunningStatus()
    {
        // CC, then a Song Select (system common), then a data-byte-only chunk
        // would have to be malformed — once running status is cancelled, the
        // bare data bytes can't be parsed and parsing terminates.
        var events = ParseAll(0xB0, 7, 100,  0xF3, 5,  10, 50);

        var e = Assert.Single(events); // only the first CC
        Assert.Equal(S1MidiEventKind.ControlChange, e.Kind);
    }

    [Fact]
    public void SystemRealtime_DoesNotCancelRunningStatus()
    {
        // CC on ch0 establishes running status, F8 (timing clock) sneaks in
        // mid-stream, then another CC reusing running status follows.
        var events = ParseAll(0xB0, 7, 100,  0xF8,  10, 50);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(S1MidiEventKind.ControlChange, e.Kind));
    }

    [Fact]
    public void Sysex_IsSkippedToTerminator()
    {
        var events = ParseAll(0xF0, 0x41, 0x10, 0x42, 0xF7,  0xB0, 7, 64);

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.ControlChange, e.Kind);
        Assert.Equal(7,  e.Data1);
        Assert.Equal(64, e.Data2);
    }

    [Fact]
    public void Sysex_WithoutTerminator_ConsumesRestOfBuffer()
    {
        // Real hardware streams sysex in chunks; an open-ended chunk shouldn't
        // throw or loop.
        var events = ParseAll(0xF0, 0x41, 0x10, 0x42, 0x01);

        Assert.Empty(events);
    }

    [Fact]
    public void TruncatedEvent_AtEnd_IsIgnored()
    {
        // CC missing its value byte. Should not throw, should not emit garbage.
        var events = ParseAll(0xB0, 7);

        Assert.Empty(events);
    }

    [Fact]
    public void DataByteWithoutPriorStatus_StopsParsing()
    {
        // No status byte ever seen — running status is 0, so we bail.
        var events = ParseAll(0x40, 0x50, 0x60);

        Assert.Empty(events);
    }

    [Fact]
    public void PitchBendAndAftertouch_AreSkippedNotEmitted()
    {
        var events = ParseAll(
            0xE0, 0x00, 0x40,    // Pitch Bend ch0 (skip)
            0xD0, 0x50,          // Channel Aftertouch ch0 (skip)
            0xA0, 60, 80,        // Poly Aftertouch ch0 (skip)
            0xB0, 7, 100);       // CC ch0 (emit)

        var e = Assert.Single(events);
        Assert.Equal(S1MidiEventKind.ControlChange, e.Kind);
    }

    [Fact]
    public void EmptyBuffer_EmitsNothing()
    {
        var events = ParseAll();
        Assert.Empty(events);
    }
}
