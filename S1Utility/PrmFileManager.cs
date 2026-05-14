using System;
using System.Collections.Generic;
using System.IO;
using S1Utility.Core;
using ChopPatternClass = S1Utility.Core.ChopPattern; // alias avoids property/type ambiguity

namespace S1Utility;

public sealed record PrmMetaArgs(string Tempo, string Transpose, string[] MotionCcLabels);

// Owns all PRM-only parameter state, data models, and patch-apply logic.
// No Avalonia dependency — pure C#, fully unit-testable.
public sealed class PrmFileManager
{
    private readonly S1Patch _patch;

    // ── Low/high-cut option strings (shared by delay and reverb advanced params) ──

    private static readonly string[] s_lowCutOpts = {
        "Flat","20","25","31.5","40","50","63","80","100","125",
        "160","200","250","315","400","500","630","800"
    };

    private static readonly string[] s_highCutOpts = {
        "Flat","630","800","1k","1.25k","1.6k","2k","2.5k","3.15k",
        "4k","5k","6.3k","8k","10k","12.5k","Flat"
    };

    // ── PRM-only effect parameters ────────────────────────────────────────────

    public IReadOnlyList<PrmParameter> DelayMain  { get; }
    public IReadOnlyList<PrmParameter> ReverbMain { get; }
    public IReadOnlyList<PrmParameter> DelayAdv   { get; }
    public IReadOnlyList<PrmParameter> ReverbAdv  { get; }

    public PrmParameter DelayTempo { get; } = new("Tempo", "DELAY_TEMPO", options: new[] {
        "128", "64t", "128d", "1_64", "32t", "64d", "1_32", "16t",
        "32d", "1_16", "8t", "16d", "1_8", "4t", "8d", "1_4" });

    public PrmParameter Leng      { get; } = new("Length",     "LENG",       prmMax: 64);
    public PrmParameter Shuffle   { get; } = new("Shuffle",    "SHUFFLE",    prmMax: 50);
    public PrmParameter Level     { get; } = new("Level",      "LEVEL",      prmMax: 127);
    public PrmParameter Scale     { get; } = new("Scale",      "SCALE",      prmMax: 7);
    public PrmParameter TempoSync { get; } = new("Tempo Sync", "TEMPO_SYNC", options: new[] { "Off", "On" });

    public PrmParameter ArpType { get; } = new("Type", "ARP_TYPE", options: new[] {
        "Off", "Up", "Down", "Up/Down", "Random", "Order" });
    public PrmParameter ArpRate { get; } = new("Rate", "ARP_RATE", prmMax: 7);

    public PrmParameter RiserSw    { get; } = new("Riser",     "RISER_SW",    options: new[] { "Off", "On" });
    public PrmParameter RiserMode  { get; } = new("Mode",      "RISER_MODE",  options: new[] { "Normal", "Rise", "Fall", "Rise+Fall" });
    public PrmParameter RiserCtrl  { get; } = new("Target",    "RISER_CTRL",  prmMax: 127);
    public PrmParameter RiserBeat  { get; } = new("Beat",      "RISER_BEAT",  prmMax: 15);
    public PrmParameter RiserShape { get; } = new("Shape",     "RISER_SHAPE", prmMax: 7);
    public PrmParameter RiserReso  { get; } = new("Resonance", "RISER_RESO",  prmMax: 100);
    public PrmParameter RiserLevel { get; } = new("Level",     "RISER_LEVEL", prmMax: 100);

    public PrmParameter DmAssignX   { get; } = new("X Assign",   "DM_ASSIGN_X",   prmMax: 15);
    public PrmParameter DmAssignY   { get; } = new("Y Assign",   "DM_ASSIGN_Y",   prmMax: 15);
    public PrmParameter DmAssignTap { get; } = new("Tap Assign", "DM_ASSIGN_TAP", prmMax: 15);
    public PrmParameter DmAssignFf  { get; } = new("FF Assign",  "DM_ASSIGN_FF",  prmMax: 15);
    public PrmParameter DmSensX     { get; } = new("X Sens",     "DM_SENS_X",     prmMax: 10);
    public PrmParameter DmSensY     { get; } = new("Y Sens",     "DM_SENS_Y",     prmMax: 10);

    // ── Data models (backing fields used internally to avoid type/property name ambiguity) ──

    private readonly ChopPattern   _chopPattern = new();
    private readonly DrawWave      _drawWave    = new();
    private readonly SequencerData _sequence    = new();

    public ChopPattern   ChopPattern => _chopPattern;
    public DrawWave      DrawWave    => _drawWave;
    public SequencerData Sequence    => _sequence;

    // ── Settings ──────────────────────────────────────────────────────────────

    public string PrmFolder   { get; set; } = "";
    public bool   PatternSync { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler<PrmMetaArgs>? MetaLoaded;
    public event EventHandler<(string Message, string Color)>? StatusChanged;
    public event EventHandler? CcSnapshotChanged;
    public event EventHandler? PatchAvailabilityChanged;

    // ── Per-slot availability (populated by RescanFolder) ─────────────────────
    // Programs (0..63) where the .PRM file exists and parses successfully.
    public HashSet<int> AvailablePrograms { get; private set; } = new();
    // Programs where the file exists but the parser threw — distinct from
    // "missing" so the UI can flag broken files vs. simply absent ones.
    public HashSet<int> MalformedPrograms { get; private set; } = new();

    // ── CC snapshot ───────────────────────────────────────────────────────────
    // Frozen CC values from the most recently loaded PRM file. Tab 2 (Patch
    // Inspector) reads from this so it shows on-disk patch state, not live
    // editor edits.

    private Dictionary<int, int> _ccSnapshot = new();
    public IReadOnlyDictionary<int, int> CcSnapshot => _ccSnapshot;

    // ── Construction ─────────────────────────────────────────────────────────

    private readonly IReadOnlyList<PrmParameter> _allPrmOnly;

    public PrmFileManager(S1Patch patch)
    {
        _patch    = patch;
        DelayMain  = BuildDelayMain();
        ReverbMain = BuildReverbMain();
        DelayAdv   = BuildDelayAdv();
        ReverbAdv  = BuildReverbAdv();

        var all = new List<PrmParameter>();
        all.AddRange(DelayMain);
        all.AddRange(DelayAdv);
        all.Add(DelayTempo);
        all.AddRange(ReverbMain);
        all.AddRange(ReverbAdv);
        all.AddRange(new[] {
            Leng, Shuffle, Level, Scale, TempoSync,
            ArpType, ArpRate,
            RiserSw, RiserMode, RiserCtrl, RiserBeat,
            RiserShape, RiserReso, RiserLevel,
            DmAssignX, DmAssignY, DmAssignTap, DmAssignFf,
            DmSensX, DmSensY,
        });
        _allPrmOnly = all;
    }

    private List<PrmParameter> BuildDelayMain() => new()
    {
        new("Sync", "DELAY_SW", options: new[] { "Off", "Sync to Tempo" }),
    };

    private List<PrmParameter> BuildReverbMain() => new()
    {
        new("Type", "REVERB_TYPE", options: new[] {
            "Ambience","Room","Hall 1","Hall 2","Plate","Spring","Modulate" }),
    };

    private List<PrmParameter> BuildDelayAdv() => new()
    {
        new("Feedback", "DELAY_FEEDBACK", prmMax: 255),
        new("Low Cut",  "DELAY_LOW_CUT",  options: s_lowCutOpts),
        new("High Cut", "DELAY_HIGH_CUT", options: s_highCutOpts),
    };

    private List<PrmParameter> BuildReverbAdv() => new()
    {
        new("Pre-Delay", "REVERB_PRE_DELAY", prmMax: 100),
        new("Density",   "REVERB_DENSITY",   prmMax: 10),
        new("Low Cut",   "REVERB_LOW_CUT",   options: s_lowCutOpts),
        new("High Cut",  "REVERB_HIGH_CUT",  options: s_highCutOpts),
    };

    // ── Apply operations ──────────────────────────────────────────────────────

    public void ApplyInitPatch()
    {
        using var stream = typeof(PrmFileManager).Assembly.GetManifestResourceStream("InitPatch.prm")!;
        using var reader = new StreamReader(stream);
        var data = PrmFileParser.Parse(reader);
        ApplyPrmData(data);

        // When Chop Type is 0 the hardware does not apply Chop Overtone to the
        // synthesis engine during a pattern load, even if the PRM stores a non-zero
        // value. Incoming MIDI CC103 bypasses that gate, so we zero it explicitly.
        bool chopTypeOff = !data.Parameters.TryGetValue("OSC_CHOP_TYPE", out var ct)
                           || ct == "0";
        if (chopTypeOff)
            _patch.HandleIncomingCC(103, 0);
    }

    public void ApplyPrmData(PrmFileData data)
    {
        foreach (var (key, rawValue) in data.Parameters)
        {
            if (!PrmCcMap.Map.TryGetValue(key, out var info)) continue;
            if (!int.TryParse(rawValue, out int prmValue)) continue;
            _patch.HandleIncomingCC(info.Cc, info.ToCc(prmValue));
        }

        _patch.HandleIncomingCC(1,  0);    // Mod Wheel = 0
        _patch.HandleIncomingCC(11, 127);  // Expression = 127

        LoadInspector(data);
    }

    // Update Tab 2 (inspector) state from a parsed PRM file without writing to
    // the live patch. Used when navigating to a dirty slot — the user's edits
    // in _patch are preserved while the inspector reflects the on-disk file.
    public bool TryLoadInspectorOnly(int program)
    {
        string path = PrmFileForProgram(program);
        if (!File.Exists(path)) return false;
        try
        {
            LoadInspector(PrmFileParser.Parse(path));
            return true;
        }
        catch { return false; }
    }

    public void LoadInspector(PrmFileData data)
    {
        LoadPrmOnly(data, DelayMain);
        LoadPrmOnly(data, new[] { DelayTempo });
        LoadPrmOnly(data, ReverbMain);
        LoadPrmOnly(data, DelayAdv);
        LoadPrmOnly(data, ReverbAdv);

        for (int w = 0; w < ChopPatternClass.Waveforms; w++)
        {
            if (data.Parameters.TryGetValue(ChopPatternClass.PrmKeys[w], out var rawStr) &&
                int.TryParse(rawStr, out int rawVal))
                _chopPattern.LoadFromPrm(w, rawVal);
        }

        var drawPts = new int[8];
        for (int i = 0; i < 8; i++)
        {
            if (data.Parameters.TryGetValue($"OSC_DRAW_P{i + 1}", out var rawStr) &&
                int.TryParse(rawStr, out int rawVal))
                drawPts[i] = rawVal;
        }
        _drawWave.LoadAll(drawPts);

        _sequence.LoadFromPrm(data);

        LoadPrmOnly(data, new[] { Leng, Shuffle, Level, Scale, TempoSync, ArpType, ArpRate });
        LoadPrmOnly(data, new[] { RiserSw, RiserMode, RiserCtrl, RiserBeat, RiserShape, RiserReso, RiserLevel });
        LoadPrmOnly(data, new[] { DmAssignX, DmAssignY, DmAssignTap, DmAssignFf, DmSensX, DmSensY });

        // Build CC snapshot directly from file data so it never reflects
        // _patch's live (possibly dirty) state.
        var snap = new Dictionary<int, int>();
        foreach (var (key, rawValue) in data.Parameters)
        {
            if (!PrmCcMap.Map.TryGetValue(key, out var info)) continue;
            if (!int.TryParse(rawValue, out int prmValue)) continue;
            snap[info.Cc] = info.ToCc(prmValue);
        }
        snap[1]  = 0;    // Mod Wheel
        snap[11] = 127;  // Expression
        _ccSnapshot = snap;
        CcSnapshotChanged?.Invoke(this, EventArgs.Empty);

        // Tempo: stored as integer × 100 (e.g. 10000 = 100.0 BPM)
        string tempo = data.Parameters.TryGetValue("TEMPO", out var tempoRaw)
                       && int.TryParse(tempoRaw, out int tempoVal)
            ? $"{tempoVal / 100.0:F1} BPM"
            : "—";

        string transpose = data.Parameters.TryGetValue("TRANSPOSE", out var trRaw)
                           && int.TryParse(trRaw, out int trVal)
            ? (trVal > 0 ? $"+{trVal}" : trVal.ToString())
            : "0";

        // Motion CC assignments (−1 = unassigned, else a CC number)
        var motionLabels = new string[8];
        for (int i = 0; i < 8; i++)
        {
            string key = $"MOTION_CC{i + 1}";
            motionLabels[i] = data.Parameters.TryGetValue(key, out var mcRaw)
                              && int.TryParse(mcRaw, out int mcVal) && mcVal >= 0
                ? (_patch.GetByCC(mcVal)?.Name ?? $"CC{mcVal}")
                : "—";
        }

        MetaLoaded?.Invoke(this, new PrmMetaArgs(tempo, transpose, motionLabels));
    }

    public bool TryLoadPatternPrm(int program)
    {
        string path = PrmFileForProgram(program);
        if (!File.Exists(path))
        {
            StatusChanged?.Invoke(this, ($"Pattern Sync: {Path.GetFileName(path)} not found in PRM folder", "#F0A040"));
            return false;
        }

        PrmFileData parsed;
        try { parsed = PrmFileParser.Parse(path); }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, ($"Pattern Sync: parse error — {ex.Message}", "#FF6B6B"));
            return false;
        }

        ApplyPrmData(parsed);
        _patch.MarkAllSynced();
        StatusChanged?.Invoke(this, ($"Pattern Sync: {Path.GetFileName(path)}", "#888888"));
        return true;
    }

    public IEnumerable<PrmParameter> AllPrmOnlyParams() => _allPrmOnly;

    // Walks all 64 program slots, partitioning them into available vs. malformed.
    // Called on folder change and on (re)connect. Synchronous: 64 small files
    // parse in well under a frame in practice.
    public void RescanFolder()
    {
        var avail     = new HashSet<int>();
        var malformed = new HashSet<int>();

        if (!string.IsNullOrEmpty(PrmFolder) && Directory.Exists(PrmFolder))
        {
            var canonical = CanonicalKeys;
            for (int program = 0; program < 64; program++)
            {
                string path = PrmFileForProgram(program);
                if (!File.Exists(path)) continue;
                try
                {
                    var data = PrmFileParser.Parse(path);
                    if (HasUnknownKeys(data, canonical))
                        malformed.Add(program);
                    else
                        avail.Add(program);
                }
                catch
                {
                    // Parser threw — file is truncated, oversized, or contains
                    // no recognisable KEY=VALUE pairs at all.
                    malformed.Add(program);
                }
            }
        }

        AvailablePrograms = avail;
        MalformedPrograms = malformed;
        PatchAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    // Lazy: parses InitPatch.prm once to capture the canonical PRM key set.
    // Used as a schema check so typo'd keys (e.g. SHUFLE for SHUFFLE) are caught
    // even when the file is syntactically valid.
    private HashSet<string>? _canonicalKeys;
    private HashSet<string> CanonicalKeys
    {
        get
        {
            if (_canonicalKeys is null)
            {
                using var stream = typeof(PrmFileManager).Assembly.GetManifestResourceStream("InitPatch.prm")!;
                using var reader = new StreamReader(stream);
                var data = PrmFileParser.Parse(reader);
                _canonicalKeys = new HashSet<string>(data.Parameters.Keys, StringComparer.Ordinal);
            }
            return _canonicalKeys;
        }
    }

    private static bool HasUnknownKeys(PrmFileData data, HashSet<string> canonical)
    {
        foreach (var key in data.Parameters.Keys)
            if (!canonical.Contains(key)) return true;
        return false;
    }

    private string PrmFileForProgram(int program)
    {
        int bank    = program / 16 + 1;
        int pattern = program % 16 + 1;
        return Path.Combine(PrmFolder, $"S1_PTN{bank}-{pattern:D2}.PRM");
    }

    private static void LoadPrmOnly(PrmFileData data, IEnumerable<PrmParameter> prms)
    {
        foreach (var p in prms)
        {
            if (data.Parameters.TryGetValue(p.PrmKey, out var raw) &&
                int.TryParse(raw, out int prmVal))
                p.LoadFromPrm(prmVal);
        }
    }
}
