using System;

namespace RolandS1Editor;

public enum EnvPhase { Off, Attack, Decay, Sustain, Release }

// Owns filter-modulation animation state: ADSR envelope and note tracking.
// LFO is excluded — its phase is unpredictable from the editor side.
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
    private double   _segmentElapsed;

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
        _segmentElapsed = 0;
    }

    public void NoteOff()
    {
        _noteCount = Math.Max(0, _noteCount - 1);
        if (_noteCount == 0)
        {
            if (_envPhase == EnvPhase.Decay)
                _envLevel = CC(30) / 127.0; // snap to sustain level
            _envLevelAtRelease = _envLevel;
            _segmentElapsed = 0;
            _envPhase = EnvPhase.Release;
        }
    }

    // Advances the ADSR envelope by dt seconds.
    // Returns true if FilterModEnabled — caller should refresh the filter curve and envelope dot.
    public bool Tick(double dt)
    {
        double attackSecs  = ModEnvTime(CC(73), 3.570);
        double decaySecs   = ModEnvTime(CC(75), 15.000);
        double sustainLvl  = CC(30) / 127.0;
        double releaseSecs = ModEnvTime(CC(72), 19.500);

        switch (_envPhase)
        {
            case EnvPhase.Attack:
            {
                _segmentElapsed = Math.Min(attackSecs, _segmentElapsed + dt);
                double t = attackSecs > 0 ? _segmentElapsed / attackSecs : 1.0;
                const double kA = 3.0;
                _envLevel = (1.0 - Math.Exp(-kA * t)) / (1.0 - Math.Exp(-kA)); // capacitor-charge curve
                if (_segmentElapsed >= attackSecs)
                {
                    _envLevel = 1.0;
                    _envPhase = EnvPhase.Decay;
                    _segmentElapsed = 0;
                }
                break;
            }
            case EnvPhase.Decay:
            {
                _segmentElapsed = Math.Min(decaySecs, _segmentElapsed + dt);
                double t    = decaySecs > 0 ? _segmentElapsed / decaySecs : 1.0;
                double end  = Math.Exp(-4.5);
                double norm = (Math.Exp(-4.5 * t) - end) / (1.0 - end); // 1→0, exponential
                _envLevel = sustainLvl + norm * (1.0 - sustainLvl);
                if (_segmentElapsed >= decaySecs)
                {
                    _envLevel = sustainLvl;
                    _segmentElapsed = 0;
                    _envPhase = EnvPhase.Sustain;
                }
                break;
            }
            case EnvPhase.Sustain:
                _envLevel = sustainLvl;
                break;
            case EnvPhase.Release:
            {
                _segmentElapsed = Math.Min(releaseSecs, _segmentElapsed + dt);
                double t    = releaseSecs > 0 ? _segmentElapsed / releaseSecs : 1.0;
                double end  = Math.Exp(-4.5);
                double norm = (Math.Exp(-4.5 * t) - end) / (1.0 - end); // 1→0, exponential
                _envLevel = norm * _envLevelAtRelease;
                if (_segmentElapsed >= releaseSecs)
                {
                    _envLevel = 0;
                    _envPhase = EnvPhase.Off;
                }
                break;
            }
        }

        if (!_filterModEnabled) return false;
        _filterModOffset = CC(24) / 127.0 * _envLevel;
        return true;
    }

    // Maps CC 0-127 to envelope time: ~1 ms at 0, maxSecs at 127 (square-law taper).
    private static double ModEnvTime(int cc, double maxSecs) => 0.001 + Math.Pow(cc / 127.0, 2.0) * maxSecs;
}
