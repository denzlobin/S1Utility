using System;

namespace S1Utility.Core;

// Piecewise-linear mapping from the raw PRM DELAY_TIME byte (0..255) to the
// millisecond value the S-1 displays in free mode (TEMPO_SYNC = 0). Built
// from hardware-verified anchors captured against the device.
public static class DelayTimeMap
{
    // Anchor pairs (raw, ms). Captured against the S-1's on-device display.
    // (255, 740) is the hardware-verified max delay time, so the curve caps
    // at 740 ms. Three roughly-linear interior segments (~1.18 / ~2.02 /
    // ~3.34 ms/raw) followed by a near-flat tail (likely a piecewise table
    // in firmware), so linear interpolation between anchors is the honest
    // representation.
    private static readonly int[] s_anchorRaw = { 0, 11, 26, 55, 106, 179, 245, 255 };
    private static readonly int[] s_anchorMs  = { 1, 14, 44, 103, 273, 517, 737, 740 };

    // Raw PRM value (0..255) → ms shown on the S-1 in free-delay mode.
    public static int RawToMs(int raw)
    {
        raw = Math.Clamp(raw, 0, 255);
        for (int i = 0; i < s_anchorRaw.Length - 1; i++)
        {
            if (raw <= s_anchorRaw[i + 1])
            {
                double t = (raw - s_anchorRaw[i]) / (double)(s_anchorRaw[i + 1] - s_anchorRaw[i]);
                return s_anchorMs[i] + (int)Math.Round(t * (s_anchorMs[i + 1] - s_anchorMs[i]));
            }
        }
        return 740;
    }

    // CC value (0..127) → ms. CC90 is the 7-bit reduction of the 8-bit raw
    // PRM byte: raw ≈ round(cc * 255 / 127). Convert and feed RawToMs so the
    // Tab 1 editor knob and the Inspector display agree across the same anchors.
    public static int CcToMs(int cc) =>
        RawToMs((int)Math.Round(Math.Clamp(cc, 0, 127) * 255.0 / 127.0));
}
