using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

// DrawWave stores 8 signed 16-bit values (-32768..32767) sign-extended from
// unsigned 16-bit PRM raw values. The lo/hi byte split into the 16 visible pads
// happens in the renderer, not here — these tests pin only the sign extension.
public class DrawWaveTests
{
    [Fact]
    public void LoadAll_Zero_StaysZero()
    {
        var wave = new DrawWave();
        wave.LoadAll(new int[] { 0, 0, 0, 0, 0, 0, 0, 0 });
        for (int i = 0; i < DrawWave.Points; i++)
            Assert.Equal(0, wave.GetPoint(i));
    }

    [Fact]
    public void LoadAll_PositiveBelow32768_StaysPositive()
    {
        var wave = new DrawWave();
        wave.LoadAll(new int[] { 100, 32767, 1, 0, 0, 0, 0, 0 });
        Assert.Equal(100,   wave.GetPoint(0));
        Assert.Equal(32767, wave.GetPoint(1));
        Assert.Equal(1,     wave.GetPoint(2));
    }

    [Fact]
    public void LoadAll_RawAtSignBoundary_BecomesNegative()
    {
        // 32768 unsigned = -32768 signed; 65535 unsigned = -1 signed.
        var wave = new DrawWave();
        wave.LoadAll(new int[] { 32768, 65535, 65407, 0, 0, 0, 0, 0 });
        Assert.Equal(-32768, wave.GetPoint(0));
        Assert.Equal(-1,     wave.GetPoint(1));
        // 65407 = 0xFF7F → high byte 0xFF (signed -1), low byte 0x7F (signed +127).
        // As a single signed 16-bit: 65407 - 65536 = -129.
        Assert.Equal(-129,   wave.GetPoint(2));
    }

    [Fact]
    public void LoadAll_TruncatesExtraValues()
    {
        var wave = new DrawWave();
        // Pass 10 values; only first 8 are loaded.
        wave.LoadAll(new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.Equal(8, wave.GetPoint(7));
    }

    [Fact]
    public void LoadAll_FiresPointsChanged()
    {
        var wave = new DrawWave();
        int fires = 0;
        wave.PointsChanged += (_, _) => fires++;

        wave.LoadAll(new int[] { 0, 0, 0, 0, 0, 0, 0, 0 });

        Assert.Equal(1, fires);
    }
}
