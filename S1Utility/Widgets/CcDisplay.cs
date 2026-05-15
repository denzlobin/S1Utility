using System;
using S1Utility.Core;

namespace S1Utility.Widgets;

// Pure value→display string formatting for CC and PRM parameters.
// No UI references — safe to call from any thread, and trivially unit-testable.
internal static class CcDisplay
{
    // D-Motion ASSIGN dropdown labels, indexed by the PRM raw value (0..8).
    public static readonly string[] DmAssignNames =
        { "Off", "Modulation", "Frequency", "Resonance", "Pitch Bend", "Pan", "Expression", "Delay Level", "Reverb Level" };

    public static string FormatSemitone(int st) => st > 0 ? $"+{st}" : st.ToString();

    public static string KnobValue(S1Parameter param) => KnobValue(param, param.Value);

    public static string KnobValue(S1Parameter param, int val)
    {
        int cc = param.CcNumber;

        return cc switch
        {
            76  => (val - 64).ToString(),
            77  => FormatSemitone(val - 64),
            // CC 3..127 → display 1.0..32.0 in 0.5 steps (OSC draw/chop multiply ratio).
            102 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}",
            // CC 0..127 → display 0..200, capped (chop overtone).
            103 => Math.Min(200, (int)Math.Round(val * 255.0 / 127)).ToString(),
            104 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}",
            _   => PrmCcMap.ByCC.TryGetValue(cc, out var e)
                       ? e.Info.ToPrm(val).ToString()
                       : val.ToString(),
        };
    }

    public static string PrmValue(PrmParameter p)
    {
        if (p.Options is not null)
            return p.Value < p.Options.Length ? p.Options[p.Value] : p.Value.ToString();

        int v = p.ToPrm();
        return p.PrmKey switch
        {
            "REVERB_PRE_DELAY"            => $"{v}ms",
            "LENG"                        => $"{v} steps",
            "RISER_RESO" or "RISER_LEVEL" => $"{v}%",
            "DM_ASSIGN_X" or "DM_ASSIGN_Y"
                or "DM_ASSIGN_TAP" or "DM_ASSIGN_FF"
                                          => v < DmAssignNames.Length ? DmAssignNames[v] : v.ToString(),
            _                             => v.ToString(),
        };
    }
}
