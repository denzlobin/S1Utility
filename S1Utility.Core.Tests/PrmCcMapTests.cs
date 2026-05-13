using System.Linq;
using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

// These tests pin the hardware-verified PRM/CC mappings. Several of the rules
// here took multiple rounds of hardware testing to establish — any "simplification"
// that fails one of these tests is almost certainly a regression.
public class PrmCcMapTests
{
    // ── Direct scaling (PrmMin=0, PrmMax=127): ToCc and ToPrm are 1:1 identity ──

    [Fact]
    public void DirectScale_RoundTrips_Identity()
    {
        var info = PrmCcMap.Map["VCO_RANGE"]; // P(14, 0, 127)
        for (int cc = 0; cc <= 127; cc++)
            Assert.Equal(cc, info.ToCc(info.ToPrm(cc)));
    }

    // ── 0-255 PRM range maps to 0-127 CC range ────────────────────────────────

    [Fact]
    public void Scale_0to255_PrmMaxBecomesCc127()
    {
        var info = PrmCcMap.Map["LFO_RATE"]; // P(3, 0, 255)
        Assert.Equal(0,   info.ToCc(0));
        Assert.Equal(127, info.ToCc(255));
    }

    [Fact]
    public void Scale_0to255_PrmMidpoint128BecomesCc64()
    {
        // FINE_TUNE: PRM 128 = hardware "0" fine tune = CC 64.
        var info = PrmCcMap.Map["FINE_TUNE"];
        Assert.Equal(64, info.ToCc(128));
    }

    // ── CC102 OSC_DRAW_MULT: PrmMin=7, PrmMax=255, CcMin=3 ───────────────────
    // Memory rule: PRM=7 → CC=3 (hardware init "1.0"). Do not change.

    [Fact]
    public void OscDrawMult_PrmMin7_MapsToCc3()
    {
        var info = PrmCcMap.Map["OSC_DRAW_MULT"];
        Assert.Equal(7,   info.PrmMin);
        Assert.Equal(255, info.PrmMax);
        Assert.Equal(3,   info.CcMin);
        Assert.Equal(3,   info.ToCc(7));
        Assert.Equal(127, info.ToCc(255));
    }

    [Fact]
    public void OscDrawMult_Cc3_RoundTripsToPrm7()
    {
        var info = PrmCcMap.Map["OSC_DRAW_MULT"];
        Assert.Equal(7, info.ToPrm(3));
    }

    [Fact]
    public void OscChopComb_PrmMin7_MapsToCc3()
    {
        // Same scaling rules as OSC_DRAW_MULT.
        var info = PrmCcMap.Map["OSC_CHOP_COMB"];
        Assert.Equal(7,   info.PrmMin);
        Assert.Equal(3,   info.CcMin);
        Assert.Equal(3,   info.ToCc(7));
        Assert.Equal(127, info.ToCc(255));
        Assert.Equal(7,   info.ToPrm(3));
    }

    // ── Signed semitone scaling: PRM -64..63 → CC 0..127 ─────────────────────

    [Fact]
    public void ChordKeyShift_PrmMinusSixtyFour_MapsToCc0()
    {
        var info = PrmCcMap.Map["CHORD_VOICE2_KEY_SHIFT"]; // P(85, -64, 63)
        Assert.Equal(0,   info.ToCc(-64));
        Assert.Equal(127, info.ToCc(63));
    }

    [Theory]
    [InlineData("CHORD_VOICE2_KEY_SHIFT")]
    [InlineData("CHORD_VOICE3_KEY_SHIFT")]
    [InlineData("CHORD_VOICE4_KEY_SHIFT")]
    public void ChordKeyShift_AllVoices_HaveSameSignedRange(string key)
    {
        var info = PrmCcMap.Map[key];
        Assert.Equal(-64, info.PrmMin);
        Assert.Equal(63,  info.PrmMax);
    }

    // ── Flag parameters: any non-zero PRM → CC 127 ────────────────────────────

    [Fact]
    public void FlagParam_PrmZero_IsCc0()
    {
        var info = PrmCcMap.Map["PORTAMENTO"]; // P(65, 0, 1)
        Assert.Equal(0, info.ToCc(0));
    }

    [Fact]
    public void FlagParam_PrmOne_IsCc127()
    {
        var info = PrmCcMap.Map["PORTAMENTO"];
        Assert.Equal(127, info.ToCc(1));
    }

    // ── Clamping behaviour ────────────────────────────────────────────────────

    [Fact]
    public void ToCc_OutOfRangePrm_ClampsToPrmMinMax()
    {
        var info = PrmCcMap.Map["LFO_RATE"]; // 0..255
        Assert.Equal(0,   info.ToCc(-100));
        Assert.Equal(127, info.ToCc(9999));
    }

    [Fact]
    public void ToPrm_OutOfRangeCc_ClampsToCcMinAnd127()
    {
        var info = PrmCcMap.Map["OSC_DRAW_MULT"]; // CcMin=3
        Assert.Equal(info.PrmMin, info.ToPrm(-50));
        Assert.Equal(info.PrmMax, info.ToPrm(500));
    }

    // ── Reverse lookup integrity ──────────────────────────────────────────────

    [Fact]
    public void ByCC_MatchesForwardMap()
    {
        foreach (var (key, info) in PrmCcMap.Map)
        {
            Assert.True(PrmCcMap.ByCC.ContainsKey(info.Cc), $"Missing reverse entry for CC{info.Cc} ({key})");
            var (reverseKey, reverseInfo) = PrmCcMap.ByCC[info.Cc];
            Assert.Equal(key,  reverseKey);
            Assert.Equal(info, reverseInfo);
        }
    }

    [Fact]
    public void AllCcNumbers_AreUnique()
    {
        var ccs = PrmCcMap.Map.Values.Select(v => v.Cc).ToList();
        Assert.Equal(ccs.Count, ccs.Distinct().Count());
    }

    // ── Spot-check that every mapped parameter survives an endpoint round-trip.
    // PRM-min and PRM-max must round-trip exactly; intermediate values may drift
    // by one due to rounding when CC range and PRM range have different cardinality.

    [Fact]
    public void Endpoints_RoundTrip_ExactlyForAllMappedParameters()
    {
        foreach (var (key, info) in PrmCcMap.Map)
        {
            Assert.Equal(info.PrmMin, info.ToPrm(info.ToCc(info.PrmMin)));
            Assert.Equal(info.PrmMax, info.ToPrm(info.ToCc(info.PrmMax)));
        }
    }
}
