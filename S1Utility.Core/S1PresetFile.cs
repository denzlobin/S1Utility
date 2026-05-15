using System.Collections.Generic;

namespace S1Utility.Core;

// The data written to / read from a .s1patch JSON file.
public class S1PresetFile
{
    public string Name { get; set; } = "Untitled";

    // One entry per CC-mapped parameter. Using a list (not dictionary) keeps the JSON
    // ordered and human-readable — each entry shows the CC name alongside its value.
    public List<ParameterEntry> Parameters { get; set; } = new();

    // PRM-only parameters (no CC) — effect settings that live only in the .PRM file.
    // Value is stored as the internal 0-127 knob range, same as PrmParameter.Value.
    public List<PrmOnlyEntry> PrmOnly { get; set; } = new();
}

// A single CC-mapped parameter snapshot inside the preset file.
public class ParameterEntry
{
    public string Name  { get; set; } = "";
    public int    Cc    { get; set; }
    public int    Value { get; set; }
}

// A single PRM-only parameter snapshot inside the preset file.
public class PrmOnlyEntry
{
    public string PrmKey { get; set; } = "";
    public int    Value  { get; set; }  // 0-127 internal knob range (PrmParameter.Value)
}
