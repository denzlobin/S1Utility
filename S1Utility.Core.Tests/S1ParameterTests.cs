using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

public class S1ParameterTests
{
    private static S1Parameter MakeKnob(int initial = 64) =>
        new("Test", cc: 100, S1Section.Filter, initialValue: initial);

    [Fact]
    public void Value_ClampsToZeroAnd127()
    {
        var p = MakeKnob();
        p.Value = -50;
        Assert.Equal(0, p.Value);

        p.Value = 500;
        Assert.Equal(127, p.Value);
    }

    [Fact]
    public void Value_SettingSameValue_DoesNotFireValueChanged()
    {
        var p = MakeKnob(initial: 30);
        int fires = 0;
        p.ValueChanged += (_, _) => fires++;

        p.Value = 30;

        Assert.Equal(0, fires);
    }

    [Fact]
    public void Value_SettingDifferentValue_FiresValueChangedOnce()
    {
        var p = MakeKnob(initial: 30);
        int fires = 0;
        p.ValueChanged += (_, _) => fires++;

        p.Value = 90;

        Assert.Equal(1, fires);
        Assert.Equal(90, p.Value);
    }

    [Fact]
    public void MarkSynced_FlipsIsSyncedOnce()
    {
        var p = MakeKnob();
        Assert.False(p.IsSynced);

        int events = 0;
        p.SyncStateChanged += (_, synced) => { if (synced) events++; };

        p.MarkSynced();
        p.MarkSynced(); // second call must be a no-op

        Assert.True(p.IsSynced);
        Assert.Equal(1, events);
    }
}
