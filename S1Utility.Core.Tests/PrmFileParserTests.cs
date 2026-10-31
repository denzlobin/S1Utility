using System.IO;
using System.Linq;
using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

public class PrmFileParserTests
{
    private static PrmFileData Parse(string body) =>
        PrmFileParser.Parse(new StringReader(body));

    [Fact]
    public void Parse_BasicKeyValue_PopulatesParameters()
    {
        var data = Parse("""
            VCF_CUTOFF=255
            VCF_RESONANCE=64
            """);

        Assert.Equal("255", data.Parameters["VCF_CUTOFF"]);
        Assert.Equal("64",  data.Parameters["VCF_RESONANCE"]);
    }

    [Fact]
    public void Parse_KeysAreUppercased()
    {
        var data = Parse("vcf_cutoff=255");
        Assert.Equal("255", data.Parameters["VCF_CUTOFF"]);
    }

    [Fact]
    public void Parse_CommentsAndBlankLines_AreSkipped()
    {
        var data = Parse("""
            ; this is a comment
            # another comment

            VCF_CUTOFF=255
            """);

        Assert.Single(data.Parameters);
        Assert.Equal("255", data.Parameters["VCF_CUTOFF"]);
    }

    [Fact]
    public void Parse_LinesWithoutEquals_AreSkipped()
    {
        var data = Parse("""
            this line has no equals sign
            VCF_CUTOFF=255
            """);

        Assert.Single(data.Parameters);
    }

    [Fact]
    public void Parse_StepNote_IsCapturedSeparately_With1BasedKey()
    {
        var data = Parse("""
            STEP_NOTE 1=NOTE1=60 VELO1=100 LENG1=50
            STEP_NOTE 16=NOTE1=72 VELO1=100 LENG1=50
            """);

        Assert.Equal("NOTE1=60 VELO1=100 LENG1=50",  data.StepNotes[1]);
        Assert.Equal("NOTE1=72 VELO1=100 LENG1=50",  data.StepNotes[16]);
        Assert.DoesNotContain("STEP_NOTE 1", data.Parameters);
    }

    [Fact]
    public void Parse_StepMotion_StepTickIsDecodedToZeroBasedIndex()
    {
        // id = step * 10 + tick. 11 → index 0, 88 → step 8 tick 8 → 63,
        // 91 → step 9 tick 1 → 64, 648 → step 64 tick 8 → 511.
        var data = Parse("""
            STEP_MOTION 11=M1=-1 PB=-32768
            STEP_MOTION 88=M1=-1 PB=-32768
            STEP_MOTION 91=M1=-1 PB=-32768
            STEP_MOTION 648=M1=-1 PB=-32768
            """);

        Assert.Equal(new[] { 0, 63, 64, 511 }, data.StepMotions.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Parse_StepMotion_OutOfRangeStepOrTick_IsSkipped()
    {
        // Step must be 1..64 and tick 1..8. 09, 90, 99, 651 are all invalid.
        // Include one valid line so the "no entries" guard doesn't throw.
        var data = Parse("""
            VCF_CUTOFF=255
            STEP_MOTION 09=M1=-1
            STEP_MOTION 90=M1=-1
            STEP_MOTION 99=M1=-1
            STEP_MOTION 651=M1=-1
            """);

        Assert.Empty(data.StepMotions);
    }

    [Fact]
    public void Parse_EmptyFile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Parse("\n\n\n"));
    }

    [Fact]
    public void Parse_NoKeyValuePairs_Throws()
    {
        // Lines with no '=' are skipped; if nothing remains we throw.
        Assert.Throws<InvalidDataException>(() => Parse("just some random text\nmore random text\n"));
    }

    [Fact]
    public void Parse_OnlyComments_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Parse("; comment one\n# comment two\n"));
    }

    [Fact]
    public void Parse_StepKeyNotMatchingStepNoteOrMotion_IsSkipped()
    {
        // Keys starting with STEP_ but not STEP_NOTE or STEP_MOTION should be ignored,
        // not added as a normal parameter. We need at least one valid line to avoid
        // the "no entries" guard.
        var data = Parse("""
            STEP_UNKNOWN 1=foo
            VCF_CUTOFF=255
            """);

        Assert.DoesNotContain("STEP_UNKNOWN 1", data.Parameters);
        Assert.Single(data.Parameters);
    }
}
