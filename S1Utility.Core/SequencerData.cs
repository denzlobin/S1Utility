using System;
using System.Linq;

namespace S1Utility.Core;

public class SequencerStep
{
    // The S-1 records 8 motion samples per step (STEP_MOTION id = step * 10 + tick).
    public const int Ticks = 8;

    // SUBSTEP index → S-1 display pattern; 'o' = part plays, '_' = silent part.
    public static readonly string[] SubStepPatterns =
    {
        "o", "oo", "ooo", "oooo", "oo__", "o_o", "_o_",
        "__o", "oo_", "ooo_", "o_o_", "_o_o", "o__o", "_oo_",
    };

    public string SubStepPattern =>
        SubStep >= 0 && SubStep < SubStepPatterns.Length ? SubStepPatterns[SubStep] : "o";

    public int[]  Notes       { get; } = new int[4] { -1, -1, -1, -1 };
    public int[]  Velocities  { get; } = new int[4];
    public int[]  Lengths     { get; } = new int[4];     // gate percent 1-100
    public int    Probability { get; set; } = 10;        // PROB, tens: 0-10 = 0-100 %
    public int    SubStep     { get; set; }              // SUBSTEP, pattern index 0-13
    public int[,] Motions     { get; } = new int[Ticks, 8]; // [tick, M1-M8], -1 = inactive
    public int[]  PitchBends  { get; } = new int[Ticks];    // -8192..8191, -32768 = inactive

    public SequencerStep() => Reset();

    public bool HasMotion(int lane)
    {
        for (int t = 0; t < Ticks; t++) if (Motions[t, lane] >= 0) return true;
        return false;
    }

    public bool HasPitchBend => PitchBends.Any(pb => pb != -32768);

    public bool HasContent()
    {
        if (Notes.Any(n => n >= 0) || HasPitchBend) return true;
        for (int m = 0; m < 8; m++) if (HasMotion(m)) return true;
        return false;
    }

    public void Reset()
    {
        Array.Fill(Notes,      -1);
        Array.Fill(Velocities,  0);
        Array.Fill(Lengths,     0);
        Probability = 10;
        SubStep     = 0;
        for (int t = 0; t < Ticks; t++)
            for (int m = 0; m < 8; m++) Motions[t, m] = -1;
        Array.Fill(PitchBends, -32768);
    }

    public void ParseFrom(string value)
    {
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 1) continue;
            var k = part[..eq];
            if (!int.TryParse(part[(eq + 1)..], out int v)) continue;

            if (k.Length >= 5 && k.StartsWith("NOTE", StringComparison.Ordinal) && int.TryParse(k[4..], out int ni) && ni is >= 1 and <= 4)
                Notes[ni - 1] = v;
            else if (k.Length >= 5 && k.StartsWith("VELO", StringComparison.Ordinal) && int.TryParse(k[4..], out int vi) && vi is >= 1 and <= 4)
                Velocities[vi - 1] = v;
            else if (k.Length >= 5 && k.StartsWith("LENG", StringComparison.Ordinal) && int.TryParse(k[4..], out int li) && li is >= 1 and <= 4)
                Lengths[li - 1] = v;
            else if (k == "PROB")
                Probability = v;
            else if (k == "SUBSTEP")
                SubStep = v;
        }
    }

    public void ParseMotionFrom(string value, int tick)
    {
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 1) continue;
            var k = part[..eq];
            if (!int.TryParse(part[(eq + 1)..], out int v)) continue;

            if (k == "PB")
                PitchBends[tick] = v;
            else if (k.Length >= 2 && k[0] == 'M' && int.TryParse(k[1..], out int mi) && mi is >= 1 and <= 8)
                Motions[tick, mi - 1] = v;
        }
    }
}

public class SequencerData
{
    public int StepCount { get; private set; } = 16;
    public int Tempo     { get; private set; }
    public int Transpose { get; private set; }
    public int Shuffle   { get; private set; }

    // CC numbers assigned to motion slots M1-M8 (-1 = unassigned).
    public int[] MotionCCs { get; } = { -1, -1, -1, -1, -1, -1, -1, -1 };

    public SequencerStep[] Steps { get; } =
        Enumerable.Range(0, 64).Select(_ => new SequencerStep()).ToArray();

    public event EventHandler? DataChanged;

    public void LoadFromPrm(PrmFileData data)
    {
        if (data.Parameters.TryGetValue("LENG",      out var l)  && int.TryParse(l,  out int leng))  StepCount = Math.Clamp(leng, 1, 64);
        if (data.Parameters.TryGetValue("TEMPO",     out var t)  && int.TryParse(t,  out int tempo)) Tempo     = tempo;
        if (data.Parameters.TryGetValue("TRANSPOSE", out var tr) && int.TryParse(tr, out int trans)) Transpose = trans;
        if (data.Parameters.TryGetValue("SHUFFLE",   out var sh) && int.TryParse(sh, out int shuf))  Shuffle   = shuf;

        for (int i = 0; i < 8; i++)
        {
            MotionCCs[i] = -1;
            if (data.Parameters.TryGetValue($"MOTION_CC{i + 1}", out var cc) && int.TryParse(cc, out int ccVal))
                MotionCCs[i] = ccVal;
        }

        foreach (var step in Steps) step.Reset();

        foreach (var (stepNum, rawValue) in data.StepNotes)
        {
            if (stepNum >= 1 && stepNum <= 64)
                Steps[stepNum - 1].ParseFrom(rawValue);
        }

        foreach (var (idx, rawValue) in data.StepMotions)
        {
            if (idx >= 0 && idx < 64 * SequencerStep.Ticks)
                Steps[idx / SequencerStep.Ticks].ParseMotionFrom(rawValue, idx % SequencerStep.Ticks);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }
}
