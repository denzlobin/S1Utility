using System.Collections.Generic;
using System.IO;

namespace RolandS1Editor;

public static class PrmFileParser
{
    // Parse a .PRM file into a key→value dictionary.
    public static PrmFileData Parse(string path)
    {
        var data = new PrmFileData();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var key   = line[..eq].Trim().ToUpperInvariant();
            var value = line[(eq + 1)..].Trim();

            // STEP_NOTE / STEP_MOTION sequences — not needed for display.
            if (key.StartsWith("STEP_", System.StringComparison.Ordinal))
                continue;

            data.Parameters[key] = value;
        }

        return data;
    }
}

// Holds the parsed contents of one .PRM file.
public class PrmFileData
{
    // Known KEY = VALUE pairs (keys are upper-case, STEP_ lines excluded).
    public Dictionary<string, string> Parameters { get; } = new();
}
