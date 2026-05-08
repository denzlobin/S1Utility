using System;
using System.Collections.Generic;
using System.IO;

namespace S1Utility.Core;

public static class PrmFileParser
{
    // Real S-1 .PRM files are ~10–30 KB. Anything larger is almost certainly
    // not a PRM file; cap before reading to avoid OOM on malicious/corrupt input.
    private const long MaxFileBytes = 1 * 1024 * 1024;
    private const int  MaxLineChars = 8 * 1024;

    public static PrmFileData Parse(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException(
                $"PRM file too large ({info.Length:N0} bytes; max {MaxFileBytes:N0}).");

        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    public static PrmFileData Parse(TextReader reader)
    {
        var data = new PrmFileData();
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            if (raw.Length > MaxLineChars)
                throw new InvalidDataException(
                    $"PRM line exceeds {MaxLineChars} characters; file is likely not a valid PRM.");
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key   = line[..eq].Trim().ToUpperInvariant();
            var value = line[(eq + 1)..].Trim();

            // "STEP_NOTE 1", "STEP_NOTE 2", â€¦ captured separately
            if (key.StartsWith("STEP_NOTE ", StringComparison.Ordinal))
            {
                if (int.TryParse(key["STEP_NOTE ".Length..], out int stepNum))
                    data.StepNotes[stepNum] = value;
            }
            // "STEP_MOTION 11" â†’ bar=1 step=1 â†’ stepIdx=0; "STEP_MOTION 88" â†’ stepIdx=63
            else if (key.StartsWith("STEP_MOTION ", StringComparison.Ordinal))
            {
                if (int.TryParse(key["STEP_MOTION ".Length..], out int encoded))
                {
                    int tens = encoded / 10, ones = encoded % 10;
                    if (tens >= 1 && tens <= 8 && ones >= 1 && ones <= 8)
                        data.StepMotions[(tens - 1) * 8 + (ones - 1)] = value;
                }
            }
            else if (!key.StartsWith("STEP_", StringComparison.Ordinal))
            {
                data.Parameters[key] = value;
            }
        }

        if (data.Parameters.Count == 0 && data.StepNotes.Count == 0 && data.StepMotions.Count == 0)
            throw new InvalidDataException(
                "No recognizable KEY=VALUE entries found; file is not a valid PRM.");

        return data;
    }
}

// Holds the parsed contents of one .PRM file.
public class PrmFileData
{
    // Standard KEY = VALUE pairs (keys are upper-case, STEP_ lines excluded).
    public Dictionary<string, string> Parameters  { get; } = new();

    // Step-sequencer notes: key = 1-based step number, value = raw composite string.
    public Dictionary<int, string>    StepNotes   { get; } = new();

    // Step-sequencer motion: key = 0-based step index (0..63), value = raw composite string.
    public Dictionary<int, string>    StepMotions { get; } = new();
}
