using S1Utility.Core;
using Xunit;

namespace S1Utility.Core.Tests;

public class PrmParameterTests
{
    [Fact]
    public void Knob_ToPrm_AtZero_IsZero()
    {
        var p = new PrmParameter("Test", "TEST", prmMax: 255);
        Assert.Equal(0, p.ToPrm());
    }

    [Fact]
    public void Knob_ToPrm_AtMax_IsPrmMax()
    {
        var p = new PrmParameter("Test", "TEST", prmMax: 255) { Value = 127 };
        Assert.Equal(255, p.ToPrm());
    }

    [Fact]
    public void Knob_LoadFromPrm_AtMax_GivesKnob127()
    {
        var p = new PrmParameter("Test", "TEST", prmMax: 255);
        p.LoadFromPrm(255);
        Assert.Equal(127, p.Value);
    }

    [Fact]
    public void Knob_LoadFromPrm_ClampsOutOfRangeRaw()
    {
        var p = new PrmParameter("Test", "TEST", prmMax: 255);
        p.LoadFromPrm(99999);
        Assert.Equal(127, p.Value);

        p.LoadFromPrm(-50);
        Assert.Equal(0, p.Value);
    }

    [Fact]
    public void Dropdown_ToPrm_IsValueDirectly()
    {
        // For dropdowns, the PRM value is the option index — no scaling.
        var p = new PrmParameter("Type", "TYPE", options: new[] { "A", "B", "C" })
        {
            Value = 2,
        };
        Assert.Equal(2, p.ToPrm());
    }

    [Fact]
    public void Dropdown_Value_ClampsToOptionsRange()
    {
        var p = new PrmParameter("Type", "TYPE", options: new[] { "A", "B", "C" })
        {
            Value = 99,
        };
        Assert.Equal(2, p.Value); // last valid index
    }

    [Fact]
    public void Knob_LoadFromPrm_FiresValueChanged()
    {
        var p = new PrmParameter("Test", "TEST", prmMax: 255);
        int fires = 0;
        p.ValueChanged += (_, _) => fires++;

        p.LoadFromPrm(255);

        Assert.Equal(1, fires);
    }
}
