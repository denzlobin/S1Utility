using System;

namespace RolandS1Editor;

public enum EnvPhase { Off, Attack, Decay, Sustain, Release }

// Owns all filter-modulation animation state: envelope, LFO, note tracking.
// No Avalonia dependency — pure C#, fully unit-testable.
public sealed class S1EditorViewModel
{
    private readonly S1Patch _patch;

    private bool     _filterModEnabled;
    private double   _filterModOffset;
    private EnvPhase _envPhase = EnvPhase.Off;
    private double   _envLevel;
    private double   _envLevelAtRelease;
    private int      _noteCount;
    private double   _lfoPhase;
    private double   _lfoRandom;

    public bool     FilterModEnabled  { get => _filterModEnabled; set { _filterModEnabled = value; if (!value) _filterModOffset = 0; } }
    public double   FilterModOffset   => _filterModOffset;
    public EnvPhase CurrentPhase      => _envPhase;
    public double   EnvLevel          => _envLevel;
    public double   EnvLevelAtRelease => _envLevelAtRelease;

    public S1EditorViewModel(S1Patch patch) => _patch = patch;

    private int CC(int cc) =>
        _patch.GetByCC(cc)?.Value ?? throw new InvalidOperationException($"Required CC {cc} not found");

    public void NoteOn()
    {
        _noteCount++;
        _envPhase = EnvPhase.Attack;
        _envLevel = 0;
        if (CC(105) == 1)
            _lfoPhase = 0;
    }

    public void NoteOff()
    {
        _noteCount = Math.Max(0, _noteCount - 1);
        if (_noteCount == 0)
        {
            if (_envPhase == EnvPhase.Decay)
                _envLevel = CC(30) / 127.0; // snap to sustain level
            _envLevelAtRelease = _envLevel;
            _envPhase = EnvPhase.Release;
        }
    }

    // Advances envelope and LFO by dt seconds.
    // Returns true if FilterModEnabled — caller should refresh the filter curve and envelope dot.
    public bool Tick(double dt)
    {
        double attackSecs  = ModEnvTime(CC(73));
        double decaySecs   = ModEnvTime(CC(75));
        double sustainLvl  = CC(30) / 127.0;
        double releaseSecs = ModEnvTime(CC(72));

        switch (_envPhase)
        {
            case EnvPhase.Attack:
                _envLevel = Math.Min(1.0, _envLevel + dt / attackSecs);
                if (_envLevel >= 1.0) _envPhase = EnvPhase.Decay;
                break;
            case EnvPhase.Decay:
                _envLevel = Math.Max(sustainLvl, _envLevel - dt * (1.0 - sustainLvl) / decaySecs);
                if (_envLevel <= sustainLvl)
                    _envPhase = (CC(29) == 0 && _noteCount > 0) ? EnvPhase.Attack : EnvPhase.Sustain;
                break;
            case EnvPhase.Sustain:
                _envLevel = sustainLvl;
                break;
            case EnvPhase.Release:
                _envLevel = Math.Max(0, _envLevel - dt / releaseSecs);
                if (_envLevel <= 0) { _envLevel = 0; _envPhase = EnvPhase.Off; }
                break;
        }

        bool   lfoSync   = CC(106) == 1;
        bool   lfoFast   = CC(79)  == 1;
        double lfoHz     = ModLfoHz(CC(3), lfoSync, lfoFast);
        double prevPhase = _lfoPhase;
        _lfoPhase = (_lfoPhase + lfoHz * dt) % 1.0;
        if (_lfoPhase < prevPhase)
        {
            _lfoRandom = Random.Shared.NextDouble() * 2.0 - 1.0;
            if (CC(29) == 0 && _noteCount > 0) // Trigger Mode = LFO, key held
            {
                _envPhase = EnvPhase.Attack;
                _envLevel = 0;
            }
        }

        double lfoVal = ModLfoValue(CC(12), _lfoPhase, _lfoRandom);

        if (!_filterModEnabled) return false;
        _filterModOffset = CC(24) / 127.0 * _envLevel + CC(25) / 127.0 * lfoVal;
        return true;
    }

    // Maps CC 0-127 to envelope time: 1 ms at 0, ~8 s at 127 (square-law taper).
    private static double ModEnvTime(int cc) => 0.001 + Math.Pow(cc / 127.0, 2.0) * 8.0;

    // Maps rate CC to Hz with exponential taper; Fast mode doubles two octaves.
    private static double ModLfoHz(int cc, bool sync, bool fast)
    {
        double hz = sync
            ? 0.06 * Math.Pow(260.0, Math.Clamp(cc, 0, 30) / 30.0)   // ~0.06–16 Hz across 31 steps
            : 0.01 * Math.Pow(2000.0, cc / 127.0);                    // ~0.01–20 Hz
        return fast ? hz * 4.0 : hz;
    }

    // Returns -1..+1 LFO value for the given waveform and phase.
    private static double ModLfoValue(int waveform, double phase, double random) => waveform switch
    {
        0 => phase * 2.0 - 1.0,
        1 => 1.0 - phase * 2.0,
        2 => phase < 0.5 ? phase * 4.0 - 1.0 : 3.0 - phase * 4.0,
        3 => phase < 0.5 ? 1.0 : -1.0,
        4 => random,
        _ => Random.Shared.NextDouble() * 2.0 - 1.0,
    };
}
