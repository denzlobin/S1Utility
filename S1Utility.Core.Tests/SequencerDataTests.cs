using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

public class SequencerStepTests
{
    [Fact]
    public void ParseFrom_SingleVoice_PopulatesNoteVelocityLength()
    {
        var step = new SequencerStep();
        step.ParseFrom("NOTE1=67 VELO1=114 LENG1=100");

        Assert.Equal(67,  step.Notes[0]);
        Assert.Equal(114, step.Velocities[0]);
        Assert.Equal(100, step.Lengths[0]);

        // Voices 2-4 remain at their reset defaults.
        Assert.Equal(-1, step.Notes[1]);
        Assert.Equal(-1, step.Notes[2]);
        Assert.Equal(-1, step.Notes[3]);
    }

    [Fact]
    public void ParseFrom_FourVoices_PopulatesAllSlots()
    {
        var step = new SequencerStep();
        step.ParseFrom("NOTE1=60 NOTE2=64 NOTE3=67 NOTE4=72 VELO1=100 VELO2=100 VELO3=100 VELO4=100");

        Assert.Equal(new[] { 60, 64, 67, 72 }, step.Notes);
    }

    [Fact]
    public void ParseFrom_RestNote_StaysMinusOne()
    {
        var step = new SequencerStep();
        // A rest step has all NOTE values = -1 in the PRM file.
        step.ParseFrom("NOTE1=-1 VELO1=0 LENG1=0");

        Assert.Equal(-1, step.Notes[0]);
    }

    [Fact]
    public void ParseFrom_IgnoresOutOfRangeVoiceIndices()
    {
        var step = new SequencerStep();
        step.ParseFrom("NOTE5=99 NOTE0=98"); // both out of [1..4]
        Assert.Equal(-1, step.Notes[0]);
    }

    [Fact]
    public void ParseMotionFrom_InactiveMotion_LeavesMinusOne()
    {
        var step = new SequencerStep();
        step.ParseMotionFrom("M1=-1 M2=-1 M3=-1 M4=-1 M5=-1 M6=-1 M7=-1 M8=-1 PB=-32768");

        for (int i = 0; i < 8; i++) Assert.Equal(-1, step.Motions[i]);
        Assert.Equal(-32768, step.PitchBend);
    }

    [Fact]
    public void ParseMotionFrom_ActiveMotion_PopulatesValues()
    {
        var step = new SequencerStep();
        step.ParseMotionFrom("M1=42 M2=-1 M8=99 PB=8192");

        Assert.Equal(42,   step.Motions[0]);
        Assert.Equal(-1,   step.Motions[1]);
        Assert.Equal(99,   step.Motions[7]);
        Assert.Equal(8192, step.PitchBend);
    }

    [Fact]
    public void Reset_RestoresInactiveDefaults()
    {
        var step = new SequencerStep();
        step.ParseFrom("NOTE1=67 VELO1=114 LENG1=100");
        step.ParseMotionFrom("M1=42 PB=8192");

        step.Reset();

        Assert.Equal(-1,     step.Notes[0]);
        Assert.Equal(0,      step.Velocities[0]);
        Assert.Equal(0,      step.Lengths[0]);
        Assert.Equal(-1,     step.Motions[0]);
        Assert.Equal(-32768, step.PitchBend);
    }
}

public class SequencerDataTests
{
    private static PrmFileData MakeMinimalPrm()
    {
        var data = new PrmFileData();
        data.Parameters["LENG"]      = "32";
        data.Parameters["TEMPO"]     = "1200"; // hardware stores 120.0 BPM as 1200
        data.Parameters["TRANSPOSE"] = "0";
        data.Parameters["SHUFFLE"]   = "50";
        data.Parameters["MOTION_CC1"] = "74";
        data.Parameters["MOTION_CC2"] = "71";
        return data;
    }

    [Fact]
    public void LoadFromPrm_PopulatesMetadata()
    {
        var seq = new SequencerData();
        seq.LoadFromPrm(MakeMinimalPrm());

        Assert.Equal(32,   seq.StepCount);
        Assert.Equal(1200, seq.Tempo);
        Assert.Equal(0,    seq.Transpose);
        Assert.Equal(50,   seq.Shuffle);
    }

    [Fact]
    public void LoadFromPrm_PopulatesMotionCcAssignments()
    {
        var seq = new SequencerData();
        seq.LoadFromPrm(MakeMinimalPrm());

        Assert.Equal(74, seq.MotionCCs[0]);
        Assert.Equal(71, seq.MotionCCs[1]);
        Assert.Equal(-1, seq.MotionCCs[7]); // not in fixture
    }

    [Fact]
    public void LoadFromPrm_ClampsStepCount_ToValidRange()
    {
        var data = MakeMinimalPrm();
        data.Parameters["LENG"] = "999";

        var seq = new SequencerData();
        seq.LoadFromPrm(data);

        Assert.Equal(64, seq.StepCount);
    }

    [Fact]
    public void LoadFromPrm_PopulatesStepNotes()
    {
        var data = MakeMinimalPrm();
        data.StepNotes[1] = "NOTE1=60 VELO1=100 LENG1=50";
        data.StepNotes[2] = "NOTE1=64 VELO1=100 LENG1=50";

        var seq = new SequencerData();
        seq.LoadFromPrm(data);

        Assert.Equal(60, seq.Steps[0].Notes[0]);
        Assert.Equal(64, seq.Steps[1].Notes[0]);
        Assert.Equal(-1, seq.Steps[2].Notes[0]); // unassigned step
    }

    [Fact]
    public void LoadFromPrm_FiresDataChanged()
    {
        var seq   = new SequencerData();
        int fires = 0;
        seq.DataChanged += (_, _) => fires++;

        seq.LoadFromPrm(MakeMinimalPrm());

        Assert.Equal(1, fires);
    }
}
