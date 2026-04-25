using System;
using System.Linq;

namespace RolandS1Editor;

public class SequencerStep
{
    public int[] Notes      { get; } = new int[4] { -1, -1, -1, -1 };
    public int[] Velocities { get; } = new int[4];
    public int[] Lengths    { get; } = new int[4];
    public int[] Motions    { get; } = new int[8];   // M1-M8, -1 = inactive
    public int   PitchBend  { get; set; } = -32768;  // -32768 = inactive

    public void Reset()
    {
        Array.Fill(Notes,      -1);
        Array.Fill(Velocities,  0);
        Array.Fill(Lengths,     0);
        Array.Fill(Motions,    -1);
        PitchBend = -32768;
    }

    public void ParseFrom(string value)
    {
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 1) continue;
            var k = part[..eq];
            if (!int.TryParse(part[(eq + 1)..], out int v)) continue;

            if (k.Length >= 5 && k[..4] == "NOTE" && int.TryParse(k[4..], out int ni) && ni is >= 1 and <= 4)
                Notes[ni - 1] = v;
            else if (k.Length >= 5 && k[..4] == "VELO" && int.TryParse(k[4..], out int vi) && vi is >= 1 and <= 4)
                Velocities[vi - 1] = v;
            else if (k.Length >= 5 && k[..4] == "LENG" && int.TryParse(k[4..], out int li) && li is >= 1 and <= 4)
                Lengths[li - 1] = v;
        }
    }

    public void ParseMotionFrom(string value)
    {
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 1) continue;
            var k = part[..eq];
            if (!int.TryParse(part[(eq + 1)..], out int v)) continue;

            if (k == "PB")
                PitchBend = v;
            else if (k.Length >= 2 && k[0] == 'M' && int.TryParse(k[1..], out int mi) && mi is >= 1 and <= 8)
                Motions[mi - 1] = v;
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

        foreach (var (stepIdx, rawValue) in data.StepMotions)
        {
            if (stepIdx >= 0 && stepIdx < 64)
                Steps[stepIdx].ParseMotionFrom(rawValue);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }
}
