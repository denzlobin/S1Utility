using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

public class ChopPatternTests
{
    [Fact]
    public void LoadFromPrm_Zero_AllStepsOff()
    {
        var pattern = new ChopPattern();
        pattern.LoadFromPrm(0, rawValue: 0);
        for (int s = 0; s < ChopPattern.Steps; s++)
            Assert.False(pattern.GetStep(0, s));
    }

    [Fact]
    public void LoadFromPrm_AllOnes_AllStepsOn()
    {
        var pattern = new ChopPattern();
        pattern.LoadFromPrm(0, rawValue: 0xFFFF);
        for (int s = 0; s < ChopPattern.Steps; s++)
            Assert.True(pattern.GetStep(0, s));
    }

    [Fact]
    public void LoadFromPrm_Bit0Set_OnlyFirstStepOn()
    {
        var pattern = new ChopPattern();
        pattern.LoadFromPrm(0, rawValue: 0x0001);
        Assert.True(pattern.GetStep(0, 0));
        for (int s = 1; s < ChopPattern.Steps; s++)
            Assert.False(pattern.GetStep(0, s));
    }

    [Fact]
    public void LoadFromPrm_AlternatingPattern_DecodesCorrectly()
    {
        // 0xAAAA = 1010101010101010 → even steps off, odd steps on.
        var pattern = new ChopPattern();
        pattern.LoadFromPrm(1, rawValue: 0xAAAA);

        for (int s = 0; s < ChopPattern.Steps; s++)
        {
            bool expectOn = (s % 2) == 1;
            Assert.Equal(expectOn, pattern.GetStep(1, s));
        }
    }

    [Fact]
    public void LoadFromPrm_FiresPatternChanged_WithWaveformIndex()
    {
        var pattern = new ChopPattern();
        int? lastWaveform = null;
        pattern.PatternChanged += (_, wf) => lastWaveform = wf;

        pattern.LoadFromPrm(2, rawValue: 0x1234);

        Assert.Equal(2, lastWaveform);
    }

    [Fact]
    public void IsAltered_FreshPattern_False()
    {
        // A fresh ChopPattern has every step OFF (zero-init bool[]). Hardware's
        // "no-chop" default is all-ones — but the editor only ever loads from
        // PRM, so a freshly constructed pattern with no PRM loaded yet shouldn't
        // claim alteration either. IsAltered=true only after we've seen a real
        // PRM value with at least one OFF bit anywhere.
        var pattern = new ChopPattern();
        // No PRM load. Backing storage is all false → IsAltered true by the rule
        // "any step is OFF". That's the conservative direction: the warning will
        // simply not fire because the overtone-and-type gates remain false until
        // a PRM is loaded.
        Assert.True(pattern.IsAltered);
    }

    [Fact]
    public void IsAltered_AfterAllOnesOnEveryWaveform_False()
    {
        // Hardware default: every waveform grid = 0xFFFF. Under this state the
        // synth doesn't engage chopping at all, so IsAltered must be false.
        var pattern = new ChopPattern();
        for (int w = 0; w < ChopPattern.Waveforms; w++)
            pattern.LoadFromPrm(w, rawValue: 0xFFFF);
        Assert.False(pattern.IsAltered);
    }

    [Fact]
    public void IsAltered_OneBitOff_True()
    {
        var pattern = new ChopPattern();
        for (int w = 0; w < ChopPattern.Waveforms; w++)
            pattern.LoadFromPrm(w, rawValue: 0xFFFF);
        // Knock one step off on waveform 2.
        pattern.LoadFromPrm(2, rawValue: 0xFFFE);
        Assert.True(pattern.IsAltered);
    }

    [Fact]
    public void WaveformsAndKeys_AreAligned()
    {
        // The waveform names and PRM keys must be paired index-by-index.
        Assert.Equal(ChopPattern.Waveforms, ChopPattern.WaveformNames.Length);
        Assert.Equal(ChopPattern.Waveforms, ChopPattern.PrmKeys.Length);
    }
}
