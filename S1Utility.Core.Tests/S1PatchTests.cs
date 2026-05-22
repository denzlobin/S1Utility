using System.Linq;
using System.Threading.Tasks;
using S1Utility.Core;
using S1Utility.Core.Tests.Support;
using Xunit;

namespace S1Utility.Core.Tests;

public class S1PatchTests
{
    [Fact]
    public void Construction_HasFiftyThreeParameters()
    {
        // The editor exposes 53 CC-mapped parameters. CC65 (legacy portamento
        // on/off) was dropped — redundant with CC31's Off/Auto/On dropdown.
        var patch = new S1Patch();
        Assert.Equal(53, patch.AllParameters.Count);
    }

    [Fact]
    public void Construction_UnsyncedCount_ExcludesHoldPedal()
    {
        // Counts the params bulk send actually pushes — only CC64 (HOLD pedal)
        // is excluded from Send All so the editor never overrides the hardware
        // sustain-pedal state. Mod Wheel + Expression are included now so Init
        // Patch resets them on the device.
        var patch = new S1Patch();
        Assert.Equal(52, patch.UnsyncedCount);
    }

    [Fact]
    public void GetByCC_ReturnsCorrectParameter()
    {
        var patch = new S1Patch();
        var cutoff = patch.GetByCC(74);
        Assert.NotNull(cutoff);
        Assert.Equal(74, cutoff!.CcNumber);
    }

    [Fact]
    public void GetByCC_UnknownCc_ReturnsNull()
    {
        var patch = new S1Patch();
        Assert.Null(patch.GetByCC(999));
    }

    [Fact]
    public void HandleIncomingCC_UpdatesParameter_WithoutSendingBack()
    {
        // Echo prevention: when the synth tells us "CC74 is 50", we update the
        // model but must not turn around and send CC74=50 back to the synth.
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport);

        patch.HandleIncomingCC(74, 50);

        Assert.Equal(50, patch.GetByCC(74)!.Value);
        Assert.Empty(transport.CcMessages);
    }

    [Fact]
    public void ParameterValue_FromUI_SendsCC()
    {
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport, channel: 3);

        patch.GetByCC(74)!.Value = 100;

        var sent = Assert.Single(transport.CcMessages);
        Assert.Equal((3, 74, 100), sent);
    }

    [Fact]
    public void MarkSynced_DecrementsUnsyncedCount()
    {
        var patch = new S1Patch();
        int before = patch.UnsyncedCount;

        patch.MarkSynced(74);

        Assert.Equal(before - 1, patch.UnsyncedCount);
    }

    [Fact]
    public void MarkAllSynced_ZerosUnsyncedCount()
    {
        var patch = new S1Patch();
        patch.MarkAllSynced();
        Assert.Equal(0, patch.UnsyncedCount);
    }

    [Fact]
    public void ResetAllSync_RestoresUnsyncedCount_ToTrackedSet()
    {
        var patch = new S1Patch();
        patch.MarkAllSynced();
        patch.ResetAllSync();
        Assert.Equal(52, patch.UnsyncedCount);
    }

    [Fact]
    public void MarkSynced_OnHoldPedal_DoesNotAffectCount()
    {
        // CC64 (HOLD) is excluded from the aggregate — its per-parameter
        // IsSynced still flips (the HOLD widget cares), but the chip count
        // must not move on transitions for it.
        var patch  = new S1Patch();
        int before = patch.UnsyncedCount;

        patch.MarkSynced(64);

        Assert.Equal(before, patch.UnsyncedCount);
        Assert.True(patch.GetByCC(64)!.IsSynced);
    }

    [Fact]
    public void SyncCountChanged_FiresOnEveryTransition()
    {
        var patch  = new S1Patch();
        int fires  = 0;
        patch.SyncCountChanged += (_, _) => fires++;

        patch.MarkSynced(74); // 1
        patch.MarkSynced(74); // already synced — no fire
        patch.MarkSynced(75); // 2

        Assert.Equal(2, fires);
    }

    // ── Bulk send rules ───────────────────────────────────────────────────────
    // CC64 (HOLD) is the only CC excluded from Send All — the editor must not
    // override the hardware sustain-pedal state. Mod Wheel (CC1) and Expression
    // (CC11) are bulk-sent so Init Patch can reset them to 0 / 127 on the device.

    [Fact]
    public async Task SendAllAsync_SkipsHoldPedal()
    {
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport);

        await patch.SendAllAsync();

        var sentCcs = transport.CcMessages.Select(m => m.Cc).ToHashSet();
        Assert.DoesNotContain(64, sentCcs);
    }

    [Fact]
    public async Task SendAllAsync_SendsModWheelAndExpression()
    {
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport);

        await patch.SendAllAsync();

        var sentCcs = transport.CcMessages.Select(m => m.Cc).ToHashSet();
        Assert.Contains(1,  sentCcs);
        Assert.Contains(11, sentCcs);
    }

    [Fact]
    public async Task SendAllAsync_SendsRemainingFiftyTwoParameters()
    {
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport);

        await patch.SendAllAsync();

        // 53 total minus the single excluded HOLD pedal CC.
        Assert.Equal(52, transport.CcMessages.Count);
    }

    [Fact]
    public async Task SendAllAsync_DoesNothing_WhenDisconnected()
    {
        var patch = new S1Patch();
        await patch.SendAllAsync(); // no transport wired → no throw, no send
    }

    [Fact]
    public void SendPanic_SendsAllSoundOff_AndAllNotesOff()
    {
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport, channel: 5);

        patch.SendPanic();

        Assert.Equal(2, transport.CcMessages.Count);
        Assert.Equal((5, 120, 0), transport.CcMessages[0]); // All Sound Off
        Assert.Equal((5, 123, 0), transport.CcMessages[1]); // All Notes Off
    }

    [Fact]
    public void SendProgramChange_RoutesToTransport_OnConfiguredChannel()
    {
        var patch     = new S1Patch();
        var transport = new FakeMidiTransport();
        patch.SetTransport(transport, channel: 7);

        patch.SendProgramChange(12);

        Assert.Equal((7, 12), Assert.Single(transport.ProgramMessages));
    }

    [Fact]
    public void Preset_RoundTrip_PreservesAllValues()
    {
        var original = new S1Patch();
        // Change a handful of values away from defaults so the round-trip is meaningful.
        original.GetByCC(74)!.Value  = 100;
        original.GetByCC(71)!.Value  = 80;
        original.GetByCC(30)!.Value  = 50;

        var preset = original.ToPreset("test");

        var restored = new S1Patch();
        restored.LoadPreset(preset);

        Assert.Equal(100, restored.GetByCC(74)!.Value);
        Assert.Equal(80,  restored.GetByCC(71)!.Value);
        Assert.Equal(50,  restored.GetByCC(30)!.Value);
    }
}
