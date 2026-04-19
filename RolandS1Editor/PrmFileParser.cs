using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RolandS1Editor;

public static class PrmFileParser
{
    // Parse a .PRM file into a key→value dictionary.
    // Lines that don't match "KEY = VALUE" (or whose key starts with STEP_) are preserved
    // verbatim in UnknownLines so the caller can round-trip them unchanged.
    public static PrmFileData Parse(string path)
    {
        var data = new PrmFileData();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            // Skip blank lines and comments.
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                data.ExtraLines.Add(raw);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                data.ExtraLines.Add(raw);
                continue;
            }

            var key   = line[..eq].Trim().ToUpperInvariant();
            var value = line[(eq + 1)..].Trim();

            // STEP_NOTE / STEP_MOTION sequences — preserve but don't parse.
            if (key.StartsWith("STEP_", StringComparison.Ordinal))
            {
                data.ExtraLines.Add(raw);
                continue;
            }

            data.Parameters[key] = value;
        }

        return data;
    }

    // Write a .PRM file. Known CC parameters come from the patch's current values;
    // prmOnly parameters come from their own scaled values;
    // rawIntegers are written verbatim as-is (used for bit-packed values like chop patterns);
    // everything else is round-tripped verbatim from lastLoaded.
    public static void Serialize(string path, S1Patch patch, PrmFileData? lastLoaded,
        IEnumerable<PrmParameter>? prmOnly = null,
        IEnumerable<(string Key, int Value)>? rawIntegers = null)
    {
        var sb = new StringBuilder();

        // Build key sets so we can exclude owned keys from the round-trip section.
        var prmOnlyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (prmOnly is not null)
            foreach (var p in prmOnly)
                prmOnlyKeys.Add(p.PrmKey);

        var rawKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rawIntegers is not null)
            foreach (var (key, _) in rawIntegers)
                rawKeys.Add(key);

        // Write all CC-mapped parameters, scaling back to PRM range.
        foreach (var (cc, (prmKey, info)) in PrmCcMap.ByCC)
        {
            var param = patch.GetByCC(cc);
            if (param is not null)
                sb.AppendLine($"{prmKey} = {info.ToPrm(param.Value)}");
        }

        // Write PRM-only parameters (no CC, PRM value scaled from 0-127 knob range).
        if (prmOnly is not null)
            foreach (var p in prmOnly)
                sb.AppendLine($"{p.PrmKey} = {p.ToPrm()}");

        // Write raw integer parameters (bit-packed etc.) verbatim.
        if (rawIntegers is not null)
            foreach (var (key, value) in rawIntegers)
                sb.AppendLine($"{key} = {value}");

        // Round-trip any keys from the last loaded file that no section above owns.
        if (lastLoaded is not null)
        {
            foreach (var (key, value) in lastLoaded.Parameters)
            {
                if (!PrmCcMap.Map.ContainsKey(key) && !prmOnlyKeys.Contains(key) && !rawKeys.Contains(key))
                    sb.AppendLine($"{key} = {value}");
            }

            // Re-append preserved lines (STEP_NOTE, STEP_MOTION, comments, etc.)
            foreach (var line in lastLoaded.ExtraLines)
                sb.AppendLine(line);
        }

        File.WriteAllText(path, sb.ToString());
    }
}

// Holds the parsed contents of one .PRM file.
public class PrmFileData
{
    // Known KEY = VALUE pairs (keys are upper-case, STEP_ lines excluded).
    public Dictionary<string, string> Parameters { get; } = new();

    // Lines that couldn't be parsed as parameters (blank, comments, STEP_ lines).
    public List<string> ExtraLines { get; } = new();
}
