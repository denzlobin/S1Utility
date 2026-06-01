using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Controls;
using S1Utility.Core;
using S1Utility.Widgets;

namespace S1Utility;

public partial class MainWindow
{
    // ── Tab 1: Realtime editor ────────────────────────────────────────────────

    // Throws if a required CC is missing — gives a clear error instead of a NullReferenceException.
    private S1Parameter RequireCC(int cc) =>
        _patch.GetByCC(cc) ?? throw new InvalidOperationException($"Required parameter CC {cc} not found in patch");

    private void BuildRealtimeEditorPanels()
    {
        new Panels.OscPanel(_patch, OscAccent, MakeOscWaveform).Populate(OscillatorPanel);
        new Panels.DrawChopPanel(_patch, OscAccent).Populate(DrawChopPanel);
        new Panels.FilterPanel(_patch, FiltAccent, MakeFilterCurve).Populate(FilterPanel);
        new Panels.EnvelopePanel(_patch, EnvAccent, MakeAdsrVisualizer).Populate(EnvelopePanel);
        new Panels.LfoPanel(_patch, LfoAccent).Populate(LfoPanel);
        new Panels.EffectsPanel(_patch, _prm, FxAccent).Populate(EffectsPanel);
        new Panels.VoicePanel(_patch, VoiceAccent).Populate(VoicePanel);
        new Panels.ChordPanel(_patch, VoiceAccent, ChordCard).Populate(ChordPanel);
        BuildPatchPanel();
    }


    // ── Oscillator waveform preview ───────────────────────────────────────────────
    //
    // Frozen one-cycle visualisation summing saw, square, sub and noise contributions.
    // The S-1 is analogue-modelled so every component carries RC-circuit character:
    // exponential capacitor discharge on every "flat" region, PolyBLEP at every reset.
    //
    // Constants below were tuned against oscilloscope captures of the device.
    // The saw is a RISING concave wave (capacitor charge → dump) — it starts at
    // SawTarget, climbs toward +1 over the cycle, then resets sharply downward
    // at the boundary. The visible spike therefore points down.
    // Square HIGH/LOW plateaus droop toward 0 but never reach it — the cycle ends
    // with HIGH ≈ +0.37 and LOW ≈ −0.37 at k=1.0.
    //
    // PolyBLEP is intentionally NOT applied here. At this canvas resolution
    // (≈64 samples/cycle) the band-limiting either lands on a single sample or
    // not at all, which produced spurious spikes rather than smoothing. Letting
    // the polyline render each discontinuity directly gives a cleaner match to
    // the captures.
    //
    // Tuning knobs if shapes drift from hardware:
    //   • SawDischargeK / SawTarget  — saw rise curvature and starting value
    //   • PulseDischargeK            — square / sub pulse droop
    //   • SubAsymPhase (0.62)        — second pulse offset for CC22 mode 0 (-2 Asym)
    //
    // Hardware CC mapping (verified against S1Patch.cs):
    //   CC19 = Square level   CC20 = Saw level   CC15 = Square PW
    //   CC21 = Sub level      CC22 = Sub Oct Type (0 = -2 Asym, 1 = -2 Sym, 2 = -1 Oct)
    //   CC23 = Noise level    CC78 = Noise Mode (0 = Pink, 1 = White)

    private const double SawDischargeK     = 1.8;   // body curvature — gentler than before, matches hardware
    private const double SawTarget         = -0.10; // body baseline sits just below zero; deep negative belongs to undershoot
    private const double SawUndershootFrac = 0.035; // fraction of cycle spent recovering from reset dip
    private const double SawUndershootMin  = -0.95; // depth of the AC-coupling dip immediately after reset
    private const double SawUndershootK    = 6.0;   // fast climb out of the dip back to baseline
    private const double PulseDischargeK   = 1.0;
    private const double PwMinDuty         = 0.008; // PW=255 still shows a thin positive spike, not zero
    private const double SubAsymPhase    = 0.62;
    private const int    OscWaveformN    = 256;
    // 4 cycles of the main oscillator fit across the canvas. Sub modes inherit
    // this scale: -1 oct → 2 sub cycles, -2 oct (sym/asym) → 1 sub cycle.
    private const int    OscMainCycles   = 4;

    private Control MakeOscWaveform()
    {
        const double W = 252, H = 34;

        var sawP       = RequireCC(20);
        var sqP        = RequireCC(19);
        var pwP        = RequireCC(15);
        var pwmSrcP    = RequireCC(16);  // 0=Envelope, 1=Manual, 2=LFO
        var subModeP   = RequireCC(22);
        var subP       = RequireCC(21);
        var noiseP     = RequireCC(23);
        var noiseModeP = RequireCC(78);
        var drawP      = RequireCC(107); // 0=Off, 1=Step, 2=Slope
        var chopOvP    = RequireCC(103); // non-zero → chop active

        var fillPath = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x1F, 0xF0, 0xA0, 0x40)) };
        var linePath = new Path
        {
            Stroke          = OscAccent,
            StrokeThickness = 1.5,
            StrokeLineCap   = PenLineCap.Round,
        };

        var canvas = new Canvas
        {
            Width        = W,
            Height       = H,
            ClipToBounds = true,
            // Transparent background so the entire canvas area registers pointer
            // hits — without it, only the rendered strokes are hit-testable and
            // the tooltip would only fire when hovering directly on the line.
            Background   = Brushes.Transparent,
        };
        canvas.Children.Add(fillPath);
        canvas.Children.Add(linePath);

        // Warning glyph in the top-right corner — appears when the rendered shape
        // cannot faithfully represent the synth's output (LFO PWM, Draw, Chop).
        // Tooltip is attached to the wrapping panel so hovering anywhere over the
        // visualisation reveals the explanation.
        var warnGlyph = new TextBlock
        {
            Text                = "⚠",
            FontSize            = 11,
            Foreground          = WarnBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 0, 2, 0),
            IsHitTestVisible    = false,
            IsVisible           = false,
        };

        var container = new Grid
        {
            Width  = W,
            Height = H,
            Margin = new Thickness(4, 0, 2, 3),
            Children = { canvas, warnGlyph },
        };

        var buf = new double[OscWaveformN];

        void Update()
        {
            // Amplitudes follow the user-spec /255 scaling. Live CC range is 0..127,
            // so absolute peaks land around 0.5 before re-normalisation — relative
            // balance between sources is what matters and the spec keeps that intact.
            double sawAmp   = sawP.Value   / 255.0;
            double sqAmp    = sqP.Value    / 255.0;
            double subAmp   = subP.Value   / 255.0;
            double noiseAmp = noiseP.Value / 255.0 * 0.35;
            // PW knob is 0..127. Hardware PW range tops out narrower than what the
            // full knob sweep would suggest — calibration against the captures put
            // the max-knob shape at roughly the internal PW=215 capture, not PW=255.
            const double PwInternalMax = 215.0 / 255.0;
            double pwVal    = pwP.Value;
            // PWM Source = Envelope: knob value sets modulation depth; live PW
            // sweeps from 0 (50% duty / square) up to the knob value as the env
            // rises, mirroring how the synth modulates pulse width over A/D/S/R.
            // Only animates when the Animations toggle is on and a note is active.
            if (pwmSrcP.Value == 0 && _envAnimator.AnimationsEnabled)
                pwVal *= _envAnimator.EnvLevel;
            double duty     = Math.Max(PwMinDuty, 0.5 * (1.0 - pwVal / 127.0 * PwInternalMax));

            Array.Clear(buf, 0, OscWaveformN);

            if (sawAmp   > 0) AddSaw(buf,    sawAmp,                    OscMainCycles);
            if (sqAmp    > 0) AddSquare(buf, duty, sqAmp,               OscMainCycles);
            if (subAmp   > 0) AddSub(buf,    subModeP.Value, subAmp);
            if (noiseAmp > 0) AddNoise(buf,  noiseModeP.Value, noiseAmp);

            double peak = 0;
            for (int i = 0; i < OscWaveformN; i++)
            {
                double a = Math.Abs(buf[i]);
                if (a > peak) peak = a;
            }

            // Soft floor on the peak so very small amplitudes (PRM ≈ 0–8) shrink
            // smoothly toward a flat line instead of snapping to one. Above the
            // floor the wave still fills 85% of the half-canvas as before.
            const double softFloor = 0.05;
            double yMid  = H / 2.0;
            double scale = (0.85 * (H / 2.0)) / Math.Max(peak, softFloor);

            var pts = new Point[OscWaveformN];
            for (int i = 0; i < OscWaveformN; i++)
                pts[i] = new Point(i * (W / (OscWaveformN - 1)), yMid - buf[i] * scale);

            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(pts[0], false);
                for (int i = 1; i < OscWaveformN; i++) ctx.LineTo(pts[i]);
                ctx.EndFigure(false);
            }
            linePath.Data = sg;

            var fillSg = new StreamGeometry();
            using (var ctx = fillSg.Open())
            {
                ctx.BeginFigure(new Point(0, yMid), false);
                for (int i = 0; i < OscWaveformN; i++) ctx.LineTo(pts[i]);
                ctx.LineTo(new Point(W, yMid));
                ctx.EndFigure(true);
            }
            fillPath.Data = fillSg;

            // Warning state: surface caveats where the rendered shape diverges
            // from what the synth actually outputs.
            //
            // Chop overtone is only audible when the grid has been altered from
            // its all-0xFFFF default (the synth's "no chop" idle state). Init
            // and many factory patches store non-zero overtone with all grid
            // masks at 0xFFFF — sound is unaffected, so warning would be noise.
            // OSC_CHOP_TYPE turned out NOT to gate audibility: a type=0 patch
            // with an altered grid still produces audible overtone once raised.
            bool chopActive = chopOvP.Value > 0
                              && _prm.ChopPattern.IsAltered;
            bool drawActive = drawP.Value != 0;
            bool unrepresented = drawActive || chopActive;
            bool lfoPwm = pwmSrcP.Value == 2;
            var messages = new List<string>();
            if (unrepresented) messages.Add("Draw / Chop output is not represented in this preview.");
            if (lfoPwm)        messages.Add("With PWM Source set to LFO the shape reflects maximum modulation extent, not the live value.");

            // LFO-PWM is a deliberate selection the user made on a clearly-labelled
            // control — surfacing a warning glyph for it adds visual noise. Keep
            // the tooltip available on hover so the caveat is still discoverable.
            warnGlyph.IsVisible = unrepresented;
            ToolTip.SetTip(canvas, messages.Count > 0 ? string.Join("\n\n", messages) : null);

            // Dim the visualisation when Draw or Chop are active: the rendered
            // shape is missing components that materially affect the output, so
            // its accuracy claim is weaker than the LFO-PWM caveat alone.
            canvas.Opacity = unrepresented ? 0.45 : 1.0;
        }

        Update();
        foreach (var p in new[] { sawP, sqP, pwP, pwmSrcP, subModeP, subP, noiseP, noiseModeP, drawP, chopOvP })
            p.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);

        // Chop grid contributes to the warning gate — fires on every PRM load.
        _prm.ChopPattern.PatternChanged += (_, _) => Dispatcher.UIThread.Post(Update);

        _oscWaveformUpdate = Update;
        return container;
    }

    // Un-normalised capacitor discharge: starts at vStart, asymptotes toward vEnd.
    // The curve does NOT reach vEnd at t=1 — that is the whole point. Real RC
    // circuits never quite reach their target inside one cycle, which is what
    // produces the residual droop we see on the hardware plateaus.
    private static double Discharge(double t, double k, double vStart, double vEnd) =>
        vEnd + (vStart - vEnd) * Math.Exp(-k * t);

    private static void AddSaw(double[] buf, double amp, int cycles)
    {
        int N = buf.Length;
        for (int i = 0; i < N; i++)
        {
            double tFull = i * cycles / (double)N;
            double t     = tFull - Math.Floor(tFull); // phase within current saw cycle
            double v;
            if (t < SawUndershootFrac)
            {
                // AC-coupling dip immediately after the reset: starts deep negative,
                // climbs quickly back to the body baseline. Produces the brief
                // downward bump just past each spike that is visible on the captures.
                double u = t / SawUndershootFrac;
                v = Discharge(u, SawUndershootK, SawUndershootMin, SawTarget);
            }
            else
            {
                // Main body: gentle concave rise from baseline toward +1. Cycle ends
                // near the peak; the next sample resets via the undershoot above,
                // so the polyline draws a tall downward spike at the boundary.
                double u = (t - SawUndershootFrac) / (1.0 - SawUndershootFrac);
                v = Discharge(u, SawDischargeK, SawTarget, 1.0);
            }
            buf[i] += v * amp;
        }
    }

    private static void AddSquare(double[] buf, double duty, double amp, int cycles)
    {
        int N = buf.Length;
        duty = Math.Clamp(duty, 0.005, 0.995);
        for (int i = 0; i < N; i++)
        {
            double tFull = i * cycles / (double)N;
            double t     = tFull - Math.Floor(tFull);
            double v = t < duty
                ? Discharge(t / duty,                  PulseDischargeK,  1.0, 0.0)
                : Discharge((t - duty) / (1.0 - duty), PulseDischargeK, -1.0, 0.0);
            buf[i] += v * amp;
        }
    }

    // Sub is a 50%-duty pulse running below the main oscillator. Octave below
    // (-1 oct) packs OscMainCycles/2 sub cycles into the buffer; -2 oct sym/asym
    // pack OscMainCycles/4. Shape is identical to the square at duty=0.5 except
    // the asym variant which adds a second compressed pulse at SubAsymPhase.
    private static void AddSub(double[] buf, int mode, double amp)
    {
        switch (mode)
        {
            case 2: AddSquare(buf, 0.5, amp, OscMainCycles / 2); return; // -1 oct
            case 1: AddSquare(buf, 0.5, amp, OscMainCycles / 4); return; // -2 oct sym
            default: AddSubAsymmetric(buf, amp, OscMainCycles / 4); return; // -2 oct asym
        }
    }

    // Two pulses per sub cycle at phase 0 and SubAsymPhase. The second pulse fits
    // into the remaining (1 − phase2) of the cycle, so it appears compressed.
    private static void AddSubAsymmetric(double[] buf, double amp, int cycles)
    {
        int N = buf.Length;
        double phase2 = SubAsymPhase;
        double w1 = phase2 * 0.5;             // first pulse high-time
        double w2 = (1.0 - phase2) * 0.5;     // second pulse high-time (compressed)
        for (int i = 0; i < N; i++)
        {
            double tFull = i * cycles / (double)N;
            double t     = tFull - Math.Floor(tFull);
            double v;
            if      (t < w1)              v = Discharge(t / w1,                                   PulseDischargeK,  1.0, 0.0);
            else if (t < phase2)          v = Discharge((t - w1) / (phase2 - w1),                 PulseDischargeK, -1.0, 0.0);
            else if (t < phase2 + w2)     v = Discharge((t - phase2) / w2,                        PulseDischargeK,  1.0, 0.0);
            else                          v = Discharge((t - phase2 - w2) / (1.0 - phase2 - w2),  PulseDischargeK, -1.0, 0.0);
            buf[i] += v * amp;
        }
    }

    private static void AddNoise(double[] buf, int mode, double amp)
    {
        int N = buf.Length;
        // Fixed seed → stable frozen frame across redraws.
        var rng = new Random(42);
        // CC78 mapping per S1Patch.cs (hardware-confirmed): 0 = Pink, 1 = White.
        bool pink = mode == 0;
        double b0 = 0, b1 = 0, b2 = 0;
        for (int i = 0; i < N; i++)
        {
            double white = rng.NextDouble() * 2.0 - 1.0;
            double v;
            if (pink)
            {
                // Paul Kellett's 3-pole pink-noise approximation.
                b0 = 0.99886 * b0 + white * 0.0555179;
                b1 = 0.99332 * b1 + white * 0.0750759;
                b2 = 0.96900 * b2 + white * 0.1538520;
                v = b0 + b1 + b2 + white * 0.5362;
            }
            else v = white;
            buf[i] += v * amp;
        }
    }

    // ── Filter curve (live lowpass SVG-style visualizer) ─────────────────────────

    private Control MakeFilterCurve(S1Parameter freqParam, S1Parameter resParam)
    {
        const double W = 252, H = 34;

        var fillPath = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x14, 0x40, 0xB0, 0xF0)) };
        var linePath = new Path { Stroke = FiltAccent, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round };
        var marker   = new Border
        {
            Width      = 1,
            Height     = H,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x40, 0xB0, 0xF0)),
        };

        var canvas = new Canvas { Width = W, Height = H, Margin = new Thickness(4, 0, 2, 3), ClipToBounds = true };
        canvas.Children.Add(fillPath);
        canvas.Children.Add(linePath);
        canvas.Children.Add(marker);

        const int FilterCurveN = 200;
        var pts = new Point[FilterCurveN]; // reused every frame; Update runs at 60fps

        void Update()
        {
            const double logMin = 1.30103; // log10(20 Hz)
            const double logMax = 4.30103; // log10(20 kHz)
            const int    N      = FilterCurveN;
            const double sigma  = 0.15;    // gaussian width in omega space

            double fN          = Math.Clamp(freqParam.Value / 127.0 + _envAnimator.FilterModOffset, 0.0, 1.0);
            double rN          = resParam.Value / 127.0;
            double cutoffFreq  = Math.Pow(10, logMin + fN * (logMax - logMin));
            double xC          = fN * W;

            // Normalise so the resonance peak always touches the canvas top; passband droops as rN rises.
            // At ω=1 the raw peak = rolloff(1) + rN = 0.7071 + rN; below rN≈0.29 that is < 1 so the
            // passband stays flat and the ceiling stays 1.0.
            double maxRaw = Math.Max(1.0, 0.707107 + rN);

            double AmplToY(double amp) => (H - 2) - Math.Clamp(amp, 0.0, 1.0) * (H - 4);

            for (int i = 0; i < N; i++)
            {
                double frac    = i / (double)(N - 1);
                double freq    = Math.Pow(10, logMin + frac * (logMax - logMin));
                double omega   = freq / cutoffFreq;
                double rawPeak = Math.Exp(-((omega - 1.0) * (omega - 1.0)) / (2 * sigma * sigma));
                double rolloff = 1.0 / Math.Sqrt(1 + Math.Pow(omega, 8)); // 4-pole ~24 dB/oct
                pts[i] = new Point(frac * W, AmplToY((rolloff + rN * rawPeak) / maxRaw));
            }

            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(pts[0], false);
                for (int i = 1; i < N; i++) ctx.LineTo(pts[i]);
                ctx.EndFigure(false);
            }
            linePath.Data = sg;

            var fillSg = new StreamGeometry();
            using (var ctx = fillSg.Open())
            {
                ctx.BeginFigure(new Point(0, H), false);
                ctx.LineTo(pts[0]);
                for (int i = 1; i < N; i++) ctx.LineTo(pts[i]);
                ctx.LineTo(new Point(W, H));
                ctx.EndFigure(true);
            }
            fillPath.Data = fillSg;

            Canvas.SetLeft(marker, xC);
        }

        _filterCurveUpdate = Update;
        Update();
        freqParam.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        resParam.ValueChanged  += (_, _) => Dispatcher.UIThread.Post(Update);

        return canvas;
    }

    // ── ADSR envelope visualizer ──────────────────────────────────────────────────

    private Control MakeAdsrVisualizer(
        S1Parameter attackP, S1Parameter decayP, S1Parameter sustainP, S1Parameter releaseP)
    {
        const double W = 252, H = 44;
        var triggerP = RequireCC(29); // Trigger Mode: 0=LFO, 1=Gate, 2=Gate+Trig

        var fillPoly   = new Polygon  { Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x70, 0xC8, 0x70)) };
        var strokePoly = new Polyline { Stroke = EnvAccent, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round };

        // Segment labels
        var lblA = new TextBlock { Text = "A", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };
        var lblD = new TextBlock { Text = "D", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };
        var lblS = new TextBlock { Text = "S", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };
        var lblR = new TextBlock { Text = "R", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };

        var canvas = new Canvas
        {
            Width      = W,
            Height     = H,
            // Transparent background so the entire canvas area registers pointer hits
            // for the warning tooltip — without it only the stroked polyline does.
            Background = Brushes.Transparent,
        };
        canvas.Children.Add(fillPoly);
        canvas.Children.Add(strokePoly);
        canvas.Children.Add(lblA);
        canvas.Children.Add(lblD);
        canvas.Children.Add(lblS);
        canvas.Children.Add(lblR);

        // Segment widths are proportional to normalised param values; sustain height = sL.
        (double aT, double dT, double hw, double rT, double sL) ComputeAdsrLayout()
        {
            const double minSeg  = 5;
            const double varPool = W - 4 * minSeg;
            double aN = attackP.Value  / 127.0;
            double dN = decayP.Value   / 127.0;
            double sN = sustainP.Value / 127.0;
            double rN = releaseP.Value / 127.0;
            double sum = aN + dN + sN + rN;
            if (sum < 0.01) { aN = dN = sN = rN = 0.25; sum = 1.0; }
            return (
                minSeg + (aN / sum) * varPool,
                minSeg + (dN / sum) * varPool,
                minSeg + (sN / sum) * varPool,
                minSeg + (rN / sum) * varPool,
                H - (sustainP.Value / 127.0) * (H - 5)
            );
        }

        const int segSamples = 16; // points per A/D/R curve

        void Update()
        {
            var (aT, dT, hw, rT, sL) = ComputeAdsrLayout();

            var pts = new List<Point>(segSamples * 3 + 4) { new(0, H) };

            // Attack: y rises from H to 3 along capacitor-charge curve.
            for (int i = 1; i <= segSamples; i++)
            {
                double t = (double)i / segSamples;
                double y = H + (3 - H) * EnvelopeAnimator.AttackCurve(t);
                pts.Add(new Point(aT * t, y));
            }
            // Decay: y falls from 3 to sL along exponential curve.
            for (int i = 1; i <= segSamples; i++)
            {
                double t = (double)i / segSamples;
                double y = 3 + (sL - 3) * (1.0 - EnvelopeAnimator.DecayReleaseCurve(t));
                pts.Add(new Point(aT + dT * t, y));
            }
            // Sustain: flat hold at sL.
            pts.Add(new Point(aT + dT + hw, sL));
            // Release: y falls from sL to H along exponential curve.
            for (int i = 1; i <= segSamples; i++)
            {
                double t = (double)i / segSamples;
                double y = sL + (H - sL) * (1.0 - EnvelopeAnimator.DecayReleaseCurve(t));
                pts.Add(new Point(aT + dT + hw + rT * t, y));
            }

            strokePoly.Points = new Avalonia.Collections.AvaloniaList<Point>(pts);
            fillPoly.Points   = new Avalonia.Collections.AvaloniaList<Point>(pts);

            Canvas.SetLeft(lblA, aT / 2 - 3);                  Canvas.SetTop(lblA, H - 9);
            Canvas.SetLeft(lblD, aT + dT / 2 - 3);             Canvas.SetTop(lblD, H - 9);
            Canvas.SetLeft(lblS, aT + dT + hw / 2 - 3);        Canvas.SetTop(lblS, H - 9);
            Canvas.SetLeft(lblR, aT + dT + hw + rT / 2 - 3);   Canvas.SetTop(lblR, H - 9);
        }

        var dot = new Ellipse
        {
            Width  = 7,
            Height = 7,
            Fill   = new SolidColorBrush(Colors.White),
            IsVisible = false,
        };
        canvas.Children.Add(dot);

        void UpdateDot()
        {
            if (!_envAnimator.AnimationsEnabled || _envAnimator.CurrentPhase == EnvPhase.Off)
            {
                dot.IsVisible = false;
                return;
            }

            var (aT2, dT2, hw2, rT2, sL2) = ComputeAdsrLayout();
            double t = _envAnimator.SegmentProgress;

            double dx, dy;
            switch (_envAnimator.CurrentPhase)
            {
                case EnvPhase.Attack:
                    dx = aT2 * t;
                    dy = H + (3 - H) * EnvelopeAnimator.AttackCurve(t);
                    break;
                case EnvPhase.Decay:
                    dx = aT2 + dT2 * t;
                    dy = 3 + (sL2 - 3) * (1.0 - EnvelopeAnimator.DecayReleaseCurve(t));
                    break;
                case EnvPhase.Sustain:
                    dx = aT2 + dT2;
                    dy = sL2;
                    break;
                case EnvPhase.Release:
                    dx = aT2 + dT2 + hw2 + rT2 * t;
                    dy = sL2 + (H - sL2) * (1.0 - EnvelopeAnimator.DecayReleaseCurve(t));
                    break;
                default:
                    dot.IsVisible = false;
                    return;
            }

            dot.IsVisible = true;
            Canvas.SetLeft(dot, dx - 3.5);
            Canvas.SetTop(dot, dy - 3.5);
        }

        // Warning glyph in the top-right — mirrors the OSC waveform overlay. Shown
        // when CC29 Trigger Mode = LFO, since the envelope is retriggered by the LFO
        // rather than the held note and the static A→D→S→R shape does not represent
        // what the synth is actually doing.
        var warnGlyph = new TextBlock
        {
            Text                = "⚠",
            FontSize            = 11,
            Foreground          = WarnBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 0, 2, 0),
            IsHitTestVisible    = false,
            IsVisible           = false,
        };

        var container = new Grid
        {
            Width  = W,
            Height = H,
            Margin = new Thickness(4, 0, 2, 4),
            Children = { canvas, warnGlyph },
        };

        void UpdateWarning()
        {
            // Only nag about LFO-driven retrigger when the animations overlay is on —
            // otherwise the static A→D→S→R shape is just the parameter readout and
            // does not pretend to represent live dynamics.
            bool lfoMode    = triggerP.Value == 0;
            bool lfoTrigger = lfoMode && _envAnimator.AnimationsEnabled;
            // Freeze the envelope in LFO mode regardless of the Animations toggle:
            // EnvLevel / FilterModOffset are read by the filter curve and OSC PWM
            // visualizers too, and a note-driven envelope cannot honestly represent
            // a hardware envelope that is being retriggered by the LFO.
            _envAnimator.EnvelopeSuspended = lfoMode;
            warnGlyph.IsVisible = lfoTrigger;
            canvas.Opacity      = lfoTrigger ? 0.45 : 1.0;
            ToolTip.SetTip(canvas, lfoTrigger
                ? "The envelope is retriggered on LFO cycles. Animation is disabled."
                : null);
            // Hide the animated dot immediately when entering LFO mode.
            if (lfoTrigger) dot.IsVisible = false;
        }

        _envelopeDotUpdate = () => { if (triggerP.Value != 0) UpdateDot(); else dot.IsVisible = false; };
        _envelopeOverlayUpdate = UpdateWarning;
        Update();
        UpdateWarning();
        attackP.ValueChanged  += (_, _) => Dispatcher.UIThread.Post(Update);
        decayP.ValueChanged   += (_, _) => Dispatcher.UIThread.Post(Update);
        sustainP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        releaseP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        triggerP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            UpdateWarning();
            // Trigger-mode change flips EnvelopeSuspended, which zeroes EnvLevel
            // and FilterModOffset. Fan out to the other envelope-driven visualizers
            // so they flush stale offset/duty values at the transition boundary;
            // OnModTimerTick alone won't redraw them once Tick starts returning false.
            _filterCurveUpdate?.Invoke();
            _oscWaveformUpdate?.Invoke();
        });

        return container;
    }


    // ── Live heuristic feature toggles ───────────────────────────────────────────

    // Returns the toggle button plus two delegates to update its visual state
    // from outside: setActive(bool) and setEnabled(bool).
    // Active state uses a neutral white-ish outline (#E0E0E8) — Patch Mirror
    // and Animations share the same treatment, no per-button accent.
    private static readonly IBrush s_heuristicActive   = new SolidColorBrush(Color.Parse("#E0E0E8"));
    private static readonly IBrush s_heuristicActiveBg = new SolidColorBrush(Color.FromArgb(0x1A, 0xE0, 0xE0, 0xE8));

    private (Border btn, HeuristicToggleState state) MakeHeuristicToggle(
        string name, string tooltip)
    {
        var led = new Border
        {
            Width             = 5,
            Height            = 5,
            CornerRadius      = new CornerRadius(2.5),
            Margin            = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameLbl = new TextBlock
        {
            Text              = name,
            FontSize          = 9.5,
            FontWeight        = FontWeight.Medium,
            LetterSpacing     = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children          = { led, nameLbl },
        };

        var btn = new Border
        {
            Height              = 26,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(9, 3),
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = nameRow,
        };
        ToolTip.SetTip(btn, tooltip);

        void SetActive(bool on)
        {
            btn.Background     = on ? s_heuristicActiveBg : Brushes.Transparent;
            btn.BorderBrush    = on ? s_heuristicActive   : new SolidColorBrush(Color.Parse("#2A2A33"));
            led.Background     = on ? s_heuristicActive   : new SolidColorBrush(Color.Parse("#2A2A2A"));
            nameLbl.Foreground = on ? s_heuristicActive   : Palette.FgMute;
        }

        void SetEnabled(bool enabled)
        {
            btn.IsEnabled = enabled;
            btn.Opacity   = enabled ? 1.0 : 0.4;
        }

        SetActive(false);
        return (btn, new HeuristicToggleState(SetActive, SetEnabled));
    }

    private bool PrmFolderHasValidFiles()
    {
        if (string.IsNullOrEmpty(_prm.PrmFolder)) return false;
        // Case-insensitive so the uppercase .PRM files the S-1 writes are found on
        // case-sensitive filesystems (Linux). A plain "*.prm" glob matches them on
        // Windows but not Linux, which left the Inspector blank with a valid folder.
        try
        {
            var opts = new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive };
            return System.IO.Directory.EnumerateFiles(_prm.PrmFolder, "*.prm", opts).Any();
        }
        catch { return false; }
    }

    // Refreshes the enabled and active state of both Patch Mirror and Animations
    // whenever the PRM folder changes or Patch Mirror is toggled.
    private void RefreshLiveFeaturesState()
    {
        bool valid         = PrmFolderHasValidFiles();
        bool patchMirrorOn = valid && _prm.PatternSync;

        _patchMirrorToggle?.SetEnabled(valid);
        _patchMirrorToggle?.SetActive(patchMirrorOn);
        _animationsToggle?.SetEnabled(patchMirrorOn);

        if (!patchMirrorOn && _envAnimator.AnimationsEnabled)
        {
            _envAnimator.AnimationsEnabled = false;
            _animationsToggle?.SetActive(false);
            _filterCurveUpdate?.Invoke();
            _envelopeDotUpdate?.Invoke();
            _envelopeOverlayUpdate?.Invoke();
            _oscWaveformUpdate?.Invoke();
        }
    }

    private void BuildLiveFeaturesPanel()
    {
        // ── Animations: filter curve + ADSR animation (requires Patch Mirror) ──
        var (animationsBtn, animationsState) = MakeHeuristicToggle(
            "ANIMATIONS",
            "Heuristic feature: animates the ADSR and its modulation targets using the editor's current " +
            "CC values as model inputs. The animation is an approximation; it responds to note events " +
            "but will not match the S-1 hardware signal path exactly.\n\n" +
            "Requires Patch Mirror enabled so the editor values reflect what is on the device.");

        _animationsToggle = animationsState;

        bool prmValid      = PrmFolderHasValidFiles();
        bool patchMirrorOn = prmValid && _prm.PatternSync;

        if (!patchMirrorOn && _envAnimator.AnimationsEnabled)
            _envAnimator.AnimationsEnabled = false;

        animationsState.SetActive(_envAnimator.AnimationsEnabled);
        animationsState.SetEnabled(patchMirrorOn);

        animationsBtn.PointerPressed += (_, _) =>
        {
            _envAnimator.AnimationsEnabled = !_envAnimator.AnimationsEnabled;
            animationsState.SetActive(_envAnimator.AnimationsEnabled);
            if (!_envAnimator.AnimationsEnabled)
            {
                _filterCurveUpdate?.Invoke();
                _envelopeDotUpdate?.Invoke();
            }
            _envelopeOverlayUpdate?.Invoke();
            SaveSettings();
        };

        // ── Patch Mirror ──────────────────────────────────────────────────────
        var (patchMirrorBtn, patchMirrorState) = MakeHeuristicToggle(
            "PATCH MIRROR",
            "Heuristic feature: loads the backed up PRM file matching the current pattern number when you switch " +
            "patterns via the editor or a MIDI Program Change.\n\n" +
            "Requires a PRM folder containing valid .PRM files. Accuracy depends on keeping the " +
            "folder in sync with what is stored on the device.");

        _patchMirrorToggle = patchMirrorState;

        patchMirrorState.SetEnabled(prmValid);
        patchMirrorState.SetActive(patchMirrorOn);

        patchMirrorBtn.PointerPressed += async (_, _) =>
        {
            if (_patternSyncDialogOpen) return;
            bool enabling = !_prm.PatternSync;
            if (enabling && !_skipPatternSyncWarning)
            {
                _patternSyncDialogOpen = true;
                bool confirmed = await ShowPatternSyncWarningAsync();
                _patternSyncDialogOpen = false;
                if (!confirmed) return;
            }
            _prm.PatternSync = enabling;
            patchMirrorState.SetActive(enabling);
            animationsState.SetEnabled(enabling);
            if (enabling)
            {
                if (_isConnected)
                    GoToMirrorInitialPatch();
                UpdateRestorePatchButton();
            }
            else
            {
                _envAnimator.AnimationsEnabled = false;
                animationsState.SetActive(false);
                _filterCurveUpdate?.Invoke();
                _envelopeDotUpdate?.Invoke();
                _envelopeOverlayUpdate?.Invoke();
                ClearDirtyTracking(); // also hides Restore button
            }
            SaveSettings();
        };

        // ── Assemble panel ────────────────────────────────────────────────────
        // LiveFeaturesPanel is a horizontal StackPanel sitting on the tab strip row;
        // buttons size to their content rather than stretching.
        LiveFeaturesPanel.Children.Add(patchMirrorBtn);
        LiveFeaturesPanel.Children.Add(animationsBtn);
    }

    private void BuildPatchPanel()
    {
        PatchGridContainer.Children.Add(new TextBlock
        {
            Text       = "PATTERNS",
            FontSize   = 10,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#555555")),
            Margin     = new Thickness(0, 0, 0, 6),
        });

        var rows = new StackPanel { Spacing = 2 };

        // Each row: BANK label column (46) + 16 stretchy button columns + mirror
        // 46px spacer on the right so the 16 tiles are visually centred under the
        // bottom dock. Tiles widen to fill whatever room the dock has.
        var colSpec = "46";
        for (int i = 0; i < 16; i++) colSpec += ",*";
        colSpec += ",46";

        for (int g = 0; g < 4; g++)
        {
            var row = new Grid
            {
                ColumnSpacing     = 3,
                ColumnDefinitions = ColumnDefinitions.Parse(colSpec),
            };

            var bankLbl = new TextBlock
            {
                Text              = $"BANK {g + 1}",
                FontSize          = 9,
                Foreground        = new SolidColorBrush(Color.Parse("#505050")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(bankLbl, 0);
            row.Children.Add(bankLbl);

            for (int p = 0; p < 16; p++)
            {
                int program = g * 16 + p;
                var btn = new Button { Content = (p + 1).ToString(), Classes = { "patch-btn" } };
                btn.Click += (_, _) => OnPatchClicked(program, btn);
                Grid.SetColumn(btn, p + 1);
                _patchButtons.Add(btn);
                row.Children.Add(btn);
            }

            rows.Children.Add(row);
        }

        PatchGridContainer.Children.Add(rows);

        _initPatchButton = new Button
        {
            Content   = "Init Patch",
            Classes   = { "toolbar" },
            IsEnabled = false,
        };
        _initPatchButton.Click += OnInitPatchClicked;
        ToolTip.SetTip(_initPatchButton, "Reset all parameters to the init patch (Ctrl+I)");

        _restorePatchButton = new Button
        {
            Content   = "Restore Patch",
            Classes   = { "toolbar" },
            IsEnabled = false,
        };
        _restorePatchButton.Click += (_, _) => OnRestorePatchClicked();
        ToolTip.SetTip(_restorePatchButton, "Reload the current slot from its PRM file (Ctrl+R)");

        // Tab-2-only PRM control. Visibility toggled by MainTabs.SelectionChanged.
        OpenPrmButton = new Button
        {
            Content   = "Open PRM File",
            Classes   = { "toolbar" },
            IsVisible = false,
        };

        var saveButton = new Button
        {
            Content = "Save Preset",
            Classes = { "toolbar" },
        };
        saveButton.Click += OnSaveClicked;
        ToolTip.SetTip(saveButton, "Save the editor state to an .s1patch file (Ctrl+S)");

        var loadButton = new Button
        {
            Content = "Load Preset",
            Classes = { "toolbar" },
        };
        loadButton.Click += OnLoadClicked;
        ToolTip.SetTip(loadButton, "Load an .s1patch file into the editor and send it to the device");

        // Tab 1 editor actions split into two halves so the gap between
        // [Init Patch | Restore Patch] and [Save Preset | Load Preset] lands
        // on the horizontal centerline of the patch grid (which lines up with
        // the boundary between pattern cells 8 and 9 of the 16-wide grid).
        var leftGroup = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 0, 3, 0),
        };
        leftGroup.Children.Add(_initPatchButton);
        leftGroup.Children.Add(_restorePatchButton);

        var rightGroup = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(3, 0, 0, 0),
        };
        rightGroup.Children.Add(saveButton);
        rightGroup.Children.Add(loadButton);

        var editorActions = new Grid
        {
            ColumnDefinitions   = new ColumnDefinitions("*,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(leftGroup,  0);
        Grid.SetColumn(rightGroup, 1);
        editorActions.Children.Add(leftGroup);
        editorActions.Children.Add(rightGroup);
        _initRestoreGroup = editorActions;

        // Tab 2 actions stay centered as a separate centered StackPanel; the
        // children carry their own IsVisible toggle, so the panel collapses on
        // Tab 1 without needing a wrapper toggle.
        var tab2Actions = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tab2Actions.Children.Add(OpenPrmButton);

        // Both groups overlap in the same row; only one is visible per tab.
        var actionRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(0, 6, 0, 0),
        };
        actionRow.Children.Add(editorActions);
        actionRow.Children.Add(tab2Actions);
        PatchGridContainer.Children.Add(actionRow);
    }


    private void BuildPrmViewerContent()
    {
        PrmViewerGrid.Children.Clear();

        // ── Row 0 — sound engine (left col) / mod & voice (right col) ─────
        // All rows content-sized (Auto). A trailing `*` stretched the last card
        // to fill, but it also let the column's content exceed the parent row's
        // squeezed `*` height and overflow downward — on Linux font metrics that
        // pushed the Riser card down over OSC Chop in the row below. Auto rows
        // take natural height, so the Viewbox scales the page to fit instead of
        // any card overlapping.
        var colA = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            RowSpacing     = 4,
        };
        var oscCard      = new Panels.OscillatorCard(_patch, _prm, OscAccent).Build();
        // 2-col row-major. Filter: Cutoff/Resonance · Env Amt/LFO Amt · Keytrk/Bend.
        // Envelope: Attack/Decay · Sustain/Release · Amp Mode/Trigger.
        // LFO: Waveform/Rate · Sync/Mode · Key Trigger/Mod Depth.
        int[] filterOrder   = { 74, 71,  24, 25,  26, 27 };
        int[] envelopeOrder = { 73, 75,  30, 72,  28, 29 };
        int[] lfoOrder      = { 12,  3, 106, 79, 105, 17 };

        Control LfoRow(int cc) =>
            cc == 3
                ? InspectorRows.BuildLfoRateViewerRow(_prm, RequireCC(3))
                : InspectorRows.BuildCcDataRow(_prm, RequireCC(cc));

        var filterCard   = Dashboard.BuildPrmCard("FILTER", FiltAccent,
            Dashboard.BuildTwoColGrid(filterOrder.Select(cc => InspectorRows.BuildCcDataRow(_prm, RequireCC(cc)))));
        var envelopeCard = Dashboard.BuildPrmCard("ENVELOPE", EnvAccent,
            Dashboard.BuildTwoColGrid(envelopeOrder.Select(cc => InspectorRows.BuildCcDataRow(_prm, RequireCC(cc)))));
        var lfoCard      = Dashboard.BuildPrmCard("LFO", LfoAccent,
            Dashboard.BuildTwoColGrid(lfoOrder.Select(LfoRow)));
        Grid.SetRow(oscCard,      0); colA.Children.Add(oscCard);
        Grid.SetRow(filterCard,   1); colA.Children.Add(filterCard);
        Grid.SetRow(envelopeCard, 2); colA.Children.Add(envelopeCard);
        Grid.SetRow(lfoCard,      3); colA.Children.Add(lfoCard);
        Grid.SetColumn(colA, 0); Grid.SetRow(colA, 0);
        PrmViewerGrid.Children.Add(colA);

        var colB = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing     = 4,
        };
        var effectsCard = new Panels.EffectsCard(_patch, _prm, FxAccent).Build();
        var voiceCard   = new Panels.VoiceCard(_patch, _prm, VoiceAccent).Build();
        var riserCard   = new Panels.RiserCard(_prm, Palette.AccentDmAlt).Build();
        Grid.SetRow(effectsCard, 0); colB.Children.Add(effectsCard);
        Grid.SetRow(voiceCard,   1); colB.Children.Add(voiceCard);
        Grid.SetRow(riserCard,   2); colB.Children.Add(riserCard);
        Grid.SetColumn(colB, 1); Grid.SetRow(colB, 0);
        PrmViewerGrid.Children.Add(colB);

        // ── Row 1 — OSC DRAW (col 0) + OSC CHOP (col 1), guaranteed same height ─
        var drawCard = new Panels.OscDrawCard(_patch, _prm, OscAccent).Build();
        var chopCard = new Panels.OscChopCard(_patch, _prm, OscAccent).Build();
        Grid.SetColumn(drawCard, 0); Grid.SetRow(drawCard, 1);
        Grid.SetColumn(chopCard, 1); Grid.SetRow(chopCard, 1);
        PrmViewerGrid.Children.Add(drawCard);
        PrmViewerGrid.Children.Add(chopCard);

        // ── Row 2 — SEQUENCER (PATTERN/ARP + VIEW STEPS) | MOTION (AUTOMATION + D-MOTION) ─
        var seqCards = new Panels.SequencerCard(
            _prm, SeqAccent, Palette.AccentDmAlt,
            _tempoLabel, _motionCcLabels, ShowSequencerWindow,
            () => _ = ExportMidiAsync());
        var seqPatternCard = seqCards.BuildPatternCard();
        var seqMotionCard  = seqCards.BuildMotionCard();
        Grid.SetColumn(seqPatternCard, 0); Grid.SetRow(seqPatternCard, 2);
        Grid.SetColumn(seqMotionCard,  1); Grid.SetRow(seqMotionCard,  2);
        PrmViewerGrid.Children.Add(seqPatternCard);
        PrmViewerGrid.Children.Add(seqMotionCard);
    }


}
