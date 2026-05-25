using System;
using S1Utility.Core;

namespace S1Utility;

public enum EnvPhase { Off, Attack, Decay, Sustain, Release }

// Drives the editor's envelope-animation state from NoteOn/NoteOff and a per-tick
// integrator. Consumed by the filter curve (FilterModOffset), the OSC PWM duty
// (EnvLevel), and the ADSR dot (CurrentPhase + SegmentProgress).
// LFO is excluded — its phase is unpredictable from the editor side.
// No Avalonia dependency — pure C#, fully unit-testable.
public sealed class EnvelopeAnimator
{
    private static readonly double ExpDecayEnd = Math.Exp(-4.5); // tail value of decay/release exp curve

    private readonly S1Patch _patch;

    private bool     _animationsEnabled;
    private bool     _envelopeSuspended;
    private double   _filterModOffset;
    private EnvPhase _envPhase = EnvPhase.Off;
    private double   _envLevel;
    private double   _envLevelAtRelease;
    private int      _noteCount;
    private double   _segmentElapsed;
    private double   _segmentProgress; // 0..1 within current segment

    // Master switch for envelope-driven animations: filter cutoff sweep, ADSR dot,
    // and OSC PWM-envelope duty modulation. Backed by the user-facing "Animations"
    // toggle. When false, Tick stops producing offsets and downstream visualizers
    // see a static state.
    public bool     AnimationsEnabled { get => _animationsEnabled; set { _animationsEnabled = value; if (!value) _filterModOffset = 0; } }
    // External "freeze the envelope" lever. Set by the view layer when the editor's
    // NoteOn/NoteOff-driven envelope cannot honestly represent what the device is
    // doing (e.g. LFO trigger mode, where the hardware retriggers on LFO cycles).
    // While suspended: Tick / NoteOn / NoteOff are no-ops, EnvLevel / FilterModOffset
    // report zero, and entering suspension resets the state machine so leaving it
    // starts from Off rather than wherever the envelope was mid-segment.
    public bool     EnvelopeSuspended
    {
        get => _envelopeSuspended;
        set
        {
            if (_envelopeSuspended == value) return;
            _envelopeSuspended = value;
            if (value)
            {
                _envPhase          = EnvPhase.Off;
                _envLevel          = 0;
                _envLevelAtRelease = 0;
                _noteCount         = 0;
                _segmentElapsed    = 0;
                _segmentProgress   = 0;
                _filterModOffset   = 0;
            }
        }
    }
    public double   FilterModOffset   => _envelopeSuspended ? 0 : _filterModOffset;
    public EnvPhase CurrentPhase      => _envPhase;
    public double   EnvLevel          => _envelopeSuspended ? 0 : _envLevel;
    public double   EnvLevelAtRelease => _envLevelAtRelease;
    public double   SegmentProgress   => _segmentProgress;

    // Capacitor-charge / RC-style attack curve, 0→1 as t goes 0→1.
    public static double AttackCurve(double t)
    {
        const double kA = 3.0;
        return (1.0 - Math.Exp(-kA * t)) / (1.0 - Math.Exp(-kA));
    }

    // Exponential fall, 1→0 as t goes 0→1. Used for both decay and release shaping.
    public static double DecayReleaseCurve(double t) =>
        (Math.Exp(-4.5 * t) - ExpDecayEnd) / (1.0 - ExpDecayEnd);

    public EnvelopeAnimator(S1Patch patch) => _patch = patch;

    private int CC(int cc) =>
        _patch.GetByCC(cc)?.Value ?? throw new InvalidOperationException($"Required CC {cc} not found");

    public void NoteOn()
    {
        if (_envelopeSuspended) return;
        _noteCount++;
        _envPhase = EnvPhase.Attack;
        _envLevel = 0;
        _segmentElapsed = 0;
        _segmentProgress = 0;
    }

    public void NoteOff()
    {
        if (_envelopeSuspended) return;
        _noteCount = Math.Max(0, _noteCount - 1);
        if (_noteCount == 0)
        {
            if (_envPhase == EnvPhase.Decay)
                _envLevel = CC(30) / 127.0; // snap to sustain level
            _envLevelAtRelease = _envLevel;
            _segmentElapsed = 0;
            _segmentProgress = 0;
            _envPhase = EnvPhase.Release;
        }
    }

    // Advances the ADSR envelope by dt seconds.
    // Returns true if AnimationsEnabled — caller should refresh the filter curve and envelope dot.
    public bool Tick(double dt)
    {
        if (_envelopeSuspended) return false;
        double attackSecs  = ModEnvTime(CC(73), 3.570, taper: 2.55);
        double decaySecs   = LookupTime(CC(75), s_decayTable);
        double sustainLvl  = CC(30) / 127.0;
        double releaseSecs = ModEnvTime(CC(72), 16.250, taper: 2.74);

        switch (_envPhase)
        {
            case EnvPhase.Attack:
            {
                _segmentElapsed = Math.Min(attackSecs, _segmentElapsed + dt);
                double t = attackSecs > 0 ? _segmentElapsed / attackSecs : 1.0;
                _segmentProgress = t;
                _envLevel = AttackCurve(t);
                if (_segmentElapsed >= attackSecs)
                {
                    _envLevel = 1.0;
                    _envPhase = EnvPhase.Decay;
                    _segmentElapsed = 0;
                    _segmentProgress = 0;
                }
                break;
            }
            case EnvPhase.Decay:
            {
                _segmentElapsed = Math.Min(decaySecs, _segmentElapsed + dt);
                double t = decaySecs > 0 ? _segmentElapsed / decaySecs : 1.0;
                _segmentProgress = t;
                _envLevel = sustainLvl + DecayReleaseCurve(t) * (1.0 - sustainLvl);
                if (_segmentElapsed >= decaySecs)
                {
                    _envLevel = sustainLvl;
                    _segmentElapsed = 0;
                    _segmentProgress = 0;
                    _envPhase = EnvPhase.Sustain;
                }
                break;
            }
            case EnvPhase.Sustain:
                _envLevel = sustainLvl;
                _segmentProgress = 0;
                break;
            case EnvPhase.Release:
            {
                _segmentElapsed = Math.Min(releaseSecs, _segmentElapsed + dt);
                double t = releaseSecs > 0 ? _segmentElapsed / releaseSecs : 1.0;
                _segmentProgress = t;
                _envLevel = DecayReleaseCurve(t) * _envLevelAtRelease;
                if (_segmentElapsed >= releaseSecs)
                {
                    _envLevel = 0;
                    _envPhase = EnvPhase.Off;
                    _segmentProgress = 0;
                }
                break;
            }
        }

        if (!_animationsEnabled) return false;
        _filterModOffset = CC(24) / 127.0 * _envLevel;
        return true;
    }

    // Maps CC 0-127 to envelope time: ~1 ms at 0, maxSecs at 127. Default taper
    // is square-law. Hardware-fitted (-60 dB protocol, 2026-05-10):
    //   Attack:  taper=2.55, max=3.570s  (CC=63 → 600 ms, CC=95 → 1.625 s)
    //   Release: taper=2.74, max=16.250s (CC=63 → 2.375 s)
    // Decay: per-segment exponents drift from 1.8 (63→95) to 5.2 (110→127),
    // so a single power law does not fit. Driven by lookup table instead.
    private static double ModEnvTime(int cc, double maxSecs, double taper = 2.0) =>
        0.001 + Math.Pow(cc / 127.0, taper) * maxSecs;

    // Hardware-measured decay times (-60 dB protocol, 2026-05-10).
    private static readonly (int Cc, double Secs)[] s_decayTable =
    {
        (0,   0.001),
        (63,  1.940),
        (95,  4.050),
        (110, 6.100),
        (127, 12.900),
    };

    // Linearly interpolates a (CC → seconds) lookup table.
    private static double LookupTime(int cc, (int Cc, double Secs)[] table)
    {
        if (cc <= table[0].Cc) return table[0].Secs;
        for (int i = 1; i < table.Length; i++)
        {
            if (cc <= table[i].Cc)
            {
                var (c0, t0) = table[i - 1];
                var (c1, t1) = table[i];
                double frac = (double)(cc - c0) / (c1 - c0);
                return t0 + frac * (t1 - t0);
            }
        }
        return table[^1].Secs;
    }
}
