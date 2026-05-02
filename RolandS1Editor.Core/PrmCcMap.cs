using System;
using System.Collections.Generic;

namespace RolandS1Editor;

// Describes how one PRM parameter maps to a MIDI CC number and how to scale it.
// PrmMin/PrmMax define the native value range in the .PRM file.
//
// Scaling rules (all use the same linear formula):
//   0-255  → CC 0-127  (ccValue = prmValue * 127 / 255, rounded)
//   0-127  → CC 0-127  (direct, 1:1)
//   0-100  → CC 0-127  (ccValue = prmValue * 127 / 100, rounded)
//   0-1    → CC 0-127  (flag: 0→0, any positive→127, because Clamp(n,0,1)=1 for n≥1)
// Reverse follows the same formula inverted.
// CcMin lets parameters with a non-zero CC floor (e.g. OSC_CHOP_COMB: CC 3–127) be represented.
public record PrmParameterInfo(int Cc, int PrmMin, int PrmMax, int CcMin = 0)
{
    // Scale a raw PRM integer to a CC value in [CcMin, 127].
    public int ToCc(int prmValue)
    {
        double norm = (double)(Math.Clamp(prmValue, PrmMin, PrmMax) - PrmMin)
                     / (PrmMax - PrmMin);
        return (int)Math.Round(CcMin + norm * (127 - CcMin));
    }

    // Scale a CC value back to the PRM range for writing.
    public int ToPrm(int ccValue)
    {
        double norm = Math.Clamp(ccValue - CcMin, 0, 127 - CcMin) / (double)(127 - CcMin);
        return (int)Math.Round(norm * (PrmMax - PrmMin) + PrmMin);
    }
}

public static class PrmCcMap
{
    private static PrmParameterInfo P(int cc, int min, int max, int ccMin = 0) => new(cc, min, max, ccMin);

    // PRM key → (CC number, PRM range).
    // MOD_WHEEL and EXPRESSION are intentionally omitted: they are preserved
    // verbatim during file round-trips but never written as CC from PRM values.
    public static readonly IReadOnlyDictionary<string, PrmParameterInfo> Map =
        new Dictionary<string, PrmParameterInfo>
        {
            // ── LFO ──────────────────────────────────────────────────────────
            { "LFO_RATE",               P(3,   0,   255) },
            { "LFO_WAVE_FORM",          P(12,  0,   127) },
            { "LFO_MOD_DEPTH",          P(17,  0,   255) },
            { "LFO_MODE",               P(79,  0,   127) },
            { "LFO_KEY_TRIG",           P(105, 0,   127) },
            { "LFO_SYNC",               P(106, 0,   127) },

            // ── Voice / Polyphony ─────────────────────────────────────────────
            { "PORTAMENTO_TIME",        P(5,   0,   255) },
            { "PAN",                    P(10,  0,   127) },
            { "PORTAMENTO_MODE",        P(31,  0,   127) },
            { "PORTAMENTO",             P(65,  0,   1)   },   // flag
            { "KBD_TRANSPOSE",          P(77,  0,   127) },
            { "ASSIGN_MODE",            P(80,  0,   127) },
            { "CHORD_VOICE2_SW",        P(81,  0,   1)   },   // flag: 0→CC0, >0→CC127
            { "CHORD_VOICE3_SW",        P(82,  0,   1)   },
            { "CHORD_VOICE4_SW",        P(83,  0,   1)   },
            { "CHORD_VOICE2_KEY_SHIFT", P(85, -64, 63) },   // signed semitones; CC = prmValue + 64
            { "CHORD_VOICE3_KEY_SHIFT", P(86, -64, 63) },
            { "CHORD_VOICE4_KEY_SHIFT", P(87, -64, 63) },
            { "CHORUS",                 P(93,  0,   127) },

            // ── Oscillator ───────────────────────────────────────────────────
            { "VCO_MOD_DEPTH",          P(13,  0,   255) },
            { "VCO_RANGE",              P(14,  0,   127) },
            { "VCO_PULSE_WIDTH",        P(15,  0,   255) },
            { "VCO_PWM_SOURCE",         P(16,  0,   127) },
            { "VCO_BEND_SENS",          P(18,  0,   127) },
            { "VCO_PWM_LEVEL",          P(19,  0,   255) },   // square oscillator level
            { "VCO_SAW_LEVEL",          P(20,  0,   255) },
            { "VCO_SUB_LEVEL",          P(21,  0,   255) },
            { "VCO_SUB_TYPE",           P(22,  0,   127) },
            { "VCO_NOISE_LEVEL",        P(23,  0,   255) },
            { "FINE_TUNE",              P(76,  0,   255) },   // 128 in PRM → CC 64
            { "NOISE_MODE",             P(78,  0,   127) },
            { "OSC_DRAW_MULT",          P(102, 0,   127) },
            { "OSC_CHOP_OVERTONE",      P(103, 0,   255) },   // PRM 0-255 native; display 0-200 via cc*200/127
            { "OSC_CHOP_COMB",          P(104, 1,   32,  3) }, // PRM 1–32 → CC 3–127 (CC floor matches hardware min)
            { "OSC_DRAW_SW",            P(107, 0,   127) },

            // ── Filter ───────────────────────────────────────────────────────
            { "VCF_ENV_DEPTH",          P(24,  0,   255) },
            { "VCF_MOD_DEPTH",          P(25,  0,   255) },
            { "VCF_KEY_FOLLOW",         P(26,  0,   255) },
            { "VCF_BEND_SENS",          P(27,  0,   127) },
            { "VCF_RESONANCE",          P(71,  0,   255) },
            { "VCF_CUTOFF",             P(74,  0,   255) },   // init=255 → CC127 (fully open)

            // ── Envelope ─────────────────────────────────────────────────────
            { "VCA_ENV_MODE",           P(28,  0,   127) },   // 0=Envelope, 1=Gate
            { "ENV_TRG_MODE",           P(29,  0,   127) },   // 0=Legato, 1=Retrigger, 2=Multi
            { "ENV_SUSTAIN",            P(30,  0,   255) },
            { "ENV_RELEASE",            P(72,  0,   255) },
            { "ENV_ATTACK",             P(73,  0,   255) },
            { "ENV_DECAY",              P(75,  0,   255) },

            // ── Effects ──────────────────────────────────────────────────────
            { "REVERB_TIME",            P(89,  0,   255) },
            { "DELAY_TIME",             P(90,  0,   255) },
            { "REVERB_LEVEL",           P(91,  0,   255) },
            { "DELAY_LEVEL",            P(92,  0,   255) },
        };

    // CC number → (PRM key, scaling info) — used by the PRM file writer.
    public static readonly IReadOnlyDictionary<int, (string Key, PrmParameterInfo Info)> ByCC;

    static PrmCcMap()
    {
        var reverse = new Dictionary<int, (string, PrmParameterInfo)>();
        foreach (var (key, info) in Map)
            reverse[info.Cc] = (key, info);
        ByCC = reverse;
    }
}
