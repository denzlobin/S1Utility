using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S1Utility;

// Persisted settings, schema-versioned. Renaming a property forces a deserialization
// failure (caught and migrated, not silently dropped); adding a property bumps
// SchemaVersion and adds a MigrateFromVN step in SettingsStore.
//
// V2 (current): renamed FilterModEnabled → AnimationsEnabled to match the user-facing
// "Animations" toggle (covers filter cutoff sweep, ADSR dot, and OSC PWM duty mod,
// not just filter modulation as the old name suggested).
//
// Defaults here are the canonical "first launch" values — MainWindow consumes the
// returned record directly, so it doesn't need to repeat them.
public sealed record SettingsV2
{
    public int    SchemaVersion          { get; init; } = 2;
    public bool   AutoConnect            { get; init; }
    public bool   AnimationsEnabled      { get; init; }
    public string PrmFolder              { get; init; } = "";
    public bool   PatternSync            { get; init; }
    public int    MidiChannel            { get; init; } = 3;
    public int    PcChannel              { get; init; } = 16;
    public bool   SkipPatternSyncWarning { get; init; }

    // Program number (0-63) sent to the synth when Patch Mirror is enabled or
    // a fresh connection is made while Mirror is already on. 0 = Bank 1 Pat 01,
    // matching the legacy hard-coded behaviour.
    public int    MirrorInitialProgram   { get; init; }

    // When true, every Patch Mirror program change (editor click, keyboard nav,
    // initial connect, or PC received from the device) is followed by a SendAll
    // that pushes the editor's CC values to the synth. Only CC-mapped parameters
    // are covered — sequencer steps, draw wave, chop pattern, D-Motion, advanced
    // FX, etc. have no MIDI path and stay whatever the device loaded.
    public bool   MirrorForcePushOnPc    { get; init; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Returns the current SettingsV2 from disk, migrating older schemas in place.
    // Any failure (missing file, bad JSON, unknown future schema) returns defaults
    // and logs — the caller never has to handle exceptions itself.
    public static SettingsV2 Load(string path)
    {
        if (!File.Exists(path)) return new SettingsV2();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            int version = 0;
            if (doc.RootElement.TryGetProperty("schemaVersion", out var verEl)
                && verEl.ValueKind == JsonValueKind.Number)
            {
                version = verEl.GetInt32();
            }

            return version switch
            {
                0 => MigrateFromV0(doc),
                1 => MigrateFromV1(doc),
                2 => Clamp(doc.Deserialize<SettingsV2>(JsonOptions) ?? new SettingsV2()),
                _ => LogUnknownVersion(version, path),
            };
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"Settings load failed: {path}", ex);
            return new SettingsV2();
        }
    }

    public static void Save(string path, SettingsV2 settings)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"Settings save failed: {path}", ex);
        }
    }

    // V0 and V1 spelled the animations flag as `filterModEnabled` on disk. V2
    // renamed the C# property to AnimationsEnabled (camelCase → `animationsEnabled`
    // on disk); both legacy schemas share the same key-by-key reader below.
    private static SettingsV2 MigrateFromV0(JsonDocument doc) => ReadLegacy(doc.RootElement);
    private static SettingsV2 MigrateFromV1(JsonDocument doc) => ReadLegacy(doc.RootElement);

    private static SettingsV2 ReadLegacy(JsonElement root)
    {
        var defaults = new SettingsV2();
        return new SettingsV2
        {
            AutoConnect            = ReadBool  (root, "autoConnect",            defaults.AutoConnect),
            AnimationsEnabled      = ReadBool  (root, "filterModEnabled",       defaults.AnimationsEnabled),
            PrmFolder              = ReadString(root, "prmFolder",              defaults.PrmFolder),
            PatternSync            = ReadBool  (root, "patternSync",            defaults.PatternSync),
            MidiChannel            = Math.Clamp(ReadInt(root, "midiChannel", defaults.MidiChannel), 1, 16),
            PcChannel              = Math.Clamp(ReadInt(root, "pcChannel",   defaults.PcChannel),   1, 16),
            SkipPatternSyncWarning = ReadBool  (root, "skipPatternSyncWarning", defaults.SkipPatternSyncWarning),
            MirrorInitialProgram   = Math.Clamp(ReadInt(root, "mirrorInitialProgram", defaults.MirrorInitialProgram), 0, 63),
            MirrorForcePushOnPc    = ReadBool  (root, "mirrorForcePushOnPc", defaults.MirrorForcePushOnPc),
        };
    }

    private static SettingsV2 Clamp(SettingsV2 s) => s with
    {
        MidiChannel          = Math.Clamp(s.MidiChannel, 1, 16),
        PcChannel            = Math.Clamp(s.PcChannel,   1, 16),
        MirrorInitialProgram = Math.Clamp(s.MirrorInitialProgram, 0, 63),
    };

    private static SettingsV2 LogUnknownVersion(int version, string path)
    {
        Log.Logger.Warn($"Settings schemaVersion {version} at '{path}' is newer than this build understands; using defaults");
        return new SettingsV2();
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : fallback;

    private static string ReadString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;
}
