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
        new Panels.FilterPanel(_patch, FiltAccent, MakeFilterCurve).Populate(FilterPanel);
        new Panels.EnvelopePanel(_patch, EnvAccent, MakeAdsrVisualizer).Populate(EnvelopePanel);
        new Panels.LfoPanel(_patch, LfoAccent).Populate(LfoPanel);
        new Panels.EffectsPanel(_patch, _prm, FxAccent).Populate(EffectsPanel);
        new Panels.VoicePanel(_patch, VoiceAccent).Populate(VoicePanel);
        BuildPatchPanel();
    }


    // ── Oscillator waveform preview ───────────────────────────────────────────────
    //
    // Frozen one-cycle visualisation summing saw, square, sub and noise contributions.
    // The S-1 is analogue-modelled so every component carries RC-circuit character:
    // exponential capacitor discharge on every "flat" region, PolyBLEP at every reset.
    //
    // Constants below were tuned against oscilloscope captures in E:/S-1 Shapes/.
    // The saw is a RISING concave wave (capacitor charge → dump) — it starts at
    // SawTarget, climbs toward +1 over the cycle, then resets sharply downward
    // at the boundary. The visible spike therefore points down (matches Saw255).
    // Square HIGH/LOW plateaus droop toward 0 but never reach it — the cycle ends
    // with HIGH ≈ +0.37 and LOW ≈ −0.37 at k=1.0 (matches SquarePW0.png).
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
            if (pwmSrcP.Value == 0 && _viewModel.FilterModEnabled)
                pwVal *= _viewModel.EnvLevel;
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
            bool drawOrChop = drawP.Value != 0 || chopOvP.Value != 0;
            bool lfoPwm     = pwmSrcP.Value == 2;
            var messages = new List<string>();
            if (drawOrChop) messages.Add("Draw / Chop output is not represented in this preview.");
            if (lfoPwm)     messages.Add("With PWM Source set to LFO the shape reflects maximum modulation extent, not the live value.");

            // LFO-PWM is a deliberate selection the user made on a clearly-labelled
            // control — surfacing a warning glyph for it adds visual noise. Keep
            // the tooltip available on hover so the caveat is still discoverable.
            warnGlyph.IsVisible = drawOrChop;
            ToolTip.SetTip(canvas, messages.Count > 0 ? string.Join("\n\n", messages) : null);

            // Dim the visualisation when Draw or Chop are active: the rendered
            // shape is missing components that materially affect the output, so
            // its accuracy claim is weaker than the LFO-PWM caveat alone.
            canvas.Opacity = drawOrChop ? 0.45 : 1.0;
        }

        Update();
        foreach (var p in new[] { sawP, sqP, pwP, pwmSrcP, subModeP, subP, noiseP, noiseModeP, drawP, chopOvP })
            p.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);

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

            double fN          = Math.Clamp(freqParam.Value / 127.0 + _viewModel.FilterModOffset, 0.0, 1.0);
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
                double y = H + (3 - H) * S1EditorViewModel.AttackCurve(t);
                pts.Add(new Point(aT * t, y));
            }
            // Decay: y falls from 3 to sL along exponential curve.
            for (int i = 1; i <= segSamples; i++)
            {
                double t = (double)i / segSamples;
                double y = 3 + (sL - 3) * (1.0 - S1EditorViewModel.DecayReleaseCurve(t));
                pts.Add(new Point(aT + dT * t, y));
            }
            // Sustain: flat hold at sL.
            pts.Add(new Point(aT + dT + hw, sL));
            // Release: y falls from sL to H along exponential curve.
            for (int i = 1; i <= segSamples; i++)
            {
                double t = (double)i / segSamples;
                double y = sL + (H - sL) * (1.0 - S1EditorViewModel.DecayReleaseCurve(t));
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
            if (!_viewModel.FilterModEnabled || _viewModel.CurrentPhase == EnvPhase.Off)
            {
                dot.IsVisible = false;
                return;
            }

            var (aT2, dT2, hw2, rT2, sL2) = ComputeAdsrLayout();
            double t = _viewModel.SegmentProgress;

            double dx, dy;
            switch (_viewModel.CurrentPhase)
            {
                case EnvPhase.Attack:
                    dx = aT2 * t;
                    dy = H + (3 - H) * S1EditorViewModel.AttackCurve(t);
                    break;
                case EnvPhase.Decay:
                    dx = aT2 + dT2 * t;
                    dy = 3 + (sL2 - 3) * (1.0 - S1EditorViewModel.DecayReleaseCurve(t));
                    break;
                case EnvPhase.Sustain:
                    dx = aT2 + dT2;
                    dy = sL2;
                    break;
                case EnvPhase.Release:
                    dx = aT2 + dT2 + hw2 + rT2 * t;
                    dy = sL2 + (H - sL2) * (1.0 - S1EditorViewModel.DecayReleaseCurve(t));
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
            bool lfoTrigger = triggerP.Value == 0 && _viewModel.FilterModEnabled;
            warnGlyph.IsVisible = lfoTrigger;
            canvas.Opacity      = lfoTrigger ? 0.45 : 1.0;
            ToolTip.SetTip(canvas, lfoTrigger
                ? "The envelope is retriggered on LFO cycles. Animation is disabled."
                : null);
            // Hide the animated dot immediately when entering LFO mode.
            if (lfoTrigger) dot.IsVisible = false;
        }

        _envelopeDotUpdate = () => { if (triggerP.Value != 0) UpdateDot(); else dot.IsVisible = false; };
        _envelopeWarningUpdate = UpdateWarning;
        Update();
        UpdateWarning();
        attackP.ValueChanged  += (_, _) => Dispatcher.UIThread.Post(Update);
        decayP.ValueChanged   += (_, _) => Dispatcher.UIThread.Post(Update);
        sustainP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        releaseP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        triggerP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(UpdateWarning);

        return container;
    }


    // ── Live heuristic feature toggles ───────────────────────────────────────────

    // Returns the toggle button plus two delegates to update its visual state
    // from outside: setActive(bool) and setEnabled(bool).
    private (Border btn, HeuristicToggleState state) MakeHeuristicToggle(
        string name, string tooltip, IBrush accent)
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
            Height              = 24,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(9, 3),
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = nameRow,
        };
        ToolTip.SetTip(btn, tooltip);

        void SetActive(bool on)
        {
            var col = ((ISolidColorBrush)accent).Color;
            btn.Background     = on ? new SolidColorBrush(Color.FromArgb(0x1A, col.R, col.G, col.B))
                                    : Brushes.Transparent;
            btn.BorderBrush    = on ? accent : new SolidColorBrush(Color.Parse("#2A2A33"));
            led.Background     = on ? accent : new SolidColorBrush(Color.Parse("#2A2A2A"));
            nameLbl.Foreground = on ? accent : Palette.FgMute;
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
        try { return System.IO.Directory.EnumerateFiles(_prm.PrmFolder, "*.prm").Any(); }
        catch { return false; }
    }

    // Refreshes the enabled and active state of both Patch Mirror and Live View
    // whenever the PRM folder changes or Patch Mirror is toggled.
    private void RefreshLiveFeaturesState()
    {
        bool valid         = PrmFolderHasValidFiles();
        bool patchMirrorOn = valid && _prm.PatternSync;

        _patchMirrorToggle?.SetEnabled(valid);
        _patchMirrorToggle?.SetActive(patchMirrorOn);
        _liveViewToggle?.SetEnabled(patchMirrorOn);

        if (!patchMirrorOn && _viewModel.FilterModEnabled)
        {
            _viewModel.FilterModEnabled = false;
            _liveViewToggle?.SetActive(false);
            _filterCurveUpdate?.Invoke();
            _envelopeDotUpdate?.Invoke();
            _envelopeWarningUpdate?.Invoke();
            _oscWaveformUpdate?.Invoke();
        }
    }

    private void BuildLiveFeaturesPanel()
    {
        // ── Auto-connect ──────────────────────────────────────────────────────
        var (autoBtn, autoState) = MakeHeuristicToggle(
            "AUTO-CONNECT",
            "Heuristic feature: searches MIDI device names for \"S-1\" and connects automatically on launch " +
            "or refresh. Any device containing \"S-1\" will match regardless of model. " +
            "Verify the right device is selected after auto-connect.",
            DmAccent);

        autoState.SetActive(_autoConnect);
        autoBtn.PointerPressed += (_, _) =>
        {
            _autoConnect = !_autoConnect;
            autoState.SetActive(_autoConnect);
            SaveSettings();
        };

        // ── Live View: filter curve + ADSR animation (requires Patch Mirror) ──
        var (liveViewBtn, liveViewState) = MakeHeuristicToggle(
            "ANIMATIONS",
            "Heuristic feature: animates the ADSR and its modulation targets using the editor's current " +
            "CC values as model inputs. The animation is an approximation; it responds to note events " +
            "but will not match the S-1 hardware signal path exactly.\n\n" +
            "Requires Patch Mirror enabled so the editor values reflect what is on the device.",
            EnvAccent);

        _liveViewToggle = liveViewState;

        bool prmValid      = PrmFolderHasValidFiles();
        bool patchMirrorOn = prmValid && _prm.PatternSync;

        if (!patchMirrorOn && _viewModel.FilterModEnabled)
            _viewModel.FilterModEnabled = false;

        liveViewState.SetActive(_viewModel.FilterModEnabled);
        liveViewState.SetEnabled(patchMirrorOn);

        liveViewBtn.PointerPressed += (_, _) =>
        {
            _viewModel.FilterModEnabled = !_viewModel.FilterModEnabled;
            liveViewState.SetActive(_viewModel.FilterModEnabled);
            if (!_viewModel.FilterModEnabled)
            {
                _filterCurveUpdate?.Invoke();
                _envelopeDotUpdate?.Invoke();
            }
            _envelopeWarningUpdate?.Invoke();
            SaveSettings();
        };

        // ── Patch Mirror ──────────────────────────────────────────────────────
        var (patchMirrorBtn, patchMirrorState) = MakeHeuristicToggle(
            "PATCH MIRROR",
            "Heuristic feature: loads the backed up PRM file matching the current pattern number when you switch " +
            "patterns via the editor or a MIDI Program Change.\n\n" +
            "Requires a PRM folder containing valid .PRM files. Accuracy depends on keeping the " +
            "folder in sync with what is stored on the device.",
            WarnBrush);

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
            liveViewState.SetEnabled(enabling);
            if (enabling)
            {
                if (_isConnected)
                    GoToPattern1();
                UpdateRestorePatchButton();
            }
            else
            {
                _viewModel.FilterModEnabled = false;
                liveViewState.SetActive(false);
                _filterCurveUpdate?.Invoke();
                _envelopeDotUpdate?.Invoke();
                _envelopeWarningUpdate?.Invoke();
                ClearDirtyTracking(); // also hides Restore button
            }
            SaveSettings();
        };

        // ── Assemble panel ────────────────────────────────────────────────────
        // LiveFeaturesPanel is a horizontal StackPanel sitting on the tab strip row;
        // buttons size to their content rather than stretching.
        LiveFeaturesPanel.Children.Add(autoBtn);
        LiveFeaturesPanel.Children.Add(patchMirrorBtn);
        LiveFeaturesPanel.Children.Add(liveViewBtn);
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

        _restorePatchButton = new Button
        {
            Content   = "Restore Patch",
            Classes   = { "toolbar" },
            IsEnabled = false,
            IsVisible = false,
        };
        _restorePatchButton.Click += (_, _) => OnRestorePatchClicked();

        // Tab-2-only PRM controls. Visibility toggled by MainTabs.SelectionChanged.
        OpenPrmButton = new Button
        {
            Content   = "Open PRM File",
            Classes   = { "toolbar" },
            IsVisible = false,
        };
        PrmInfoToggle = new Button
        {
            Content   = "ⓘ",
            Classes   = { "toolbar" },
            Padding   = new Thickness(6, 4),
            IsVisible = false,
        };
        ToolTip.SetTip(PrmInfoToggle, "Show / hide file access instructions");

        _initRestoreGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
        };
        _initRestoreGroup.Children.Add(_initPatchButton);
        _initRestoreGroup.Children.Add(_restorePatchButton);

        var actionRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 6, 0, 0),
        };
        actionRow.Children.Add(_initRestoreGroup);
        actionRow.Children.Add(OpenPrmButton);
        actionRow.Children.Add(PrmInfoToggle);
        PatchGridContainer.Children.Add(actionRow);

        PrmInfoText = new TextBlock
        {
            IsVisible     = false,
            Foreground    = new SolidColorBrush(Color.Parse("#7878A0")),
            FontSize      = 9.5,
            TextWrapping  = TextWrapping.Wrap,
            MaxWidth      = 860,
            TextAlignment = TextAlignment.Center,
            Margin        = new Thickness(0, 4, 0, 0),
            Text          = "To access patch files on the S-1: connect USB, then hold PLAY while powering on. " +
                            "Files are in the BACKUP folder. To restore: copy files to RESTORE folder, eject, then press HOLD on the device.",
        };
        PatchGridContainer.Children.Add(PrmInfoText);
    }


    // ── Tab 2: PRM Viewer ─────────────────────────────────────────────────────

    // Accent palette specific to the PRM viewer dashboard cards.
    // Defined here so the chrome (header dot + accent gradient) is single-sourced.
    private static IBrush PrmRiserAccent => Palette.AccentFx;     // riser shares teal with effects per spec
    private static IBrush PrmDmAccent    => Palette.AccentDmAlt;

    // Cell brushes for the OSC CHOP LED grid (amber on, dark off).
    // Tab 2 chop-grid background: distinct from BgApp by a single channel, kept
    // as its own resource because it's unique to the CHOP visualizer cells.
    private static readonly IBrush s_chopLedOff = new SolidColorBrush(Color.Parse("#1A1A20"));

    // Dashboard tokens forward to Palette so the canonical hex lives only in App.axaml.
    private static IBrush s_chopVizBg      => Palette.BgApp;
    private static IBrush s_dashLabelBrush => Palette.FgLabel;
    private static IBrush s_dashValueBrush => Palette.FgVal;
    private static IBrush s_dashRowBorder  => Palette.BdRow;
    private static IBrush s_dashCardBg     => Palette.BgCard;
    private static IBrush s_dashCardBorder => Palette.BdCard;

    // Mono font stack for value labels — Cascadia/Consolas only (no IBM Plex dependency).
    private static readonly FontFamily s_dashMonoFont = Palette.MonoFont;

    private void BuildPrmViewerContent()
    {
        PrmViewerGrid.Children.Clear();

        // ── Row 0 — top columns: sound engine (left) / mod & voice (right) ─
        // Grids (not StackPanels) so the last card in each column can stretch
        // to fill the row's `*` height — no blank gap at the column bottom.
        var colA = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            RowSpacing     = 4,
        };
        var oscCard      = BuildOscillatorCard();
        // Row-major fills the 3-col grid: index ordering chosen so columns read top-to-bottom
        // as requested (Col1 / Col2 / Col3).
        int[] filterOrder   = { 74, 24, 26,    71, 25, 27 };
        int[] envelopeOrder = { 73, 30, 28,    75, 72, 29 };
        int[] lfoOrder      = { 12, 79, 105,   3, 106, 17 };

        Control LfoRow(int cc) =>
            cc == 3 ? BuildLfoRateViewerRow(RequireCC(3)) : BuildCcDataRow(RequireCC(cc));

        var filterCard   = Dashboard.BuildPrmCard("FILTER", FiltAccent,
            Dashboard.BuildThreeColGrid(filterOrder.Select(cc => (Control)BuildCcDataRow(RequireCC(cc)))));
        var envelopeCard = Dashboard.BuildPrmCard("ENVELOPE", EnvAccent,
            Dashboard.BuildThreeColGrid(envelopeOrder.Select(cc => (Control)BuildCcDataRow(RequireCC(cc)))));
        var lfoCard      = Dashboard.BuildPrmCard("LFO", LfoAccent,
            Dashboard.BuildThreeColGrid(lfoOrder.Select(LfoRow)));
        Grid.SetRow(oscCard,      0); colA.Children.Add(oscCard);
        Grid.SetRow(filterCard,   1); colA.Children.Add(filterCard);
        Grid.SetRow(envelopeCard, 2); colA.Children.Add(envelopeCard);
        Grid.SetRow(lfoCard,      3); colA.Children.Add(lfoCard);
        Grid.SetColumn(colA, 0); Grid.SetRow(colA, 0);
        PrmViewerGrid.Children.Add(colA);

        var colB = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing     = 4,
        };
        var riserCard = Dashboard.BuildPrmCard("RISER", PrmRiserAccent, Dashboard.BuildThreeColGrid(new[]
        {
            BuildPrmDataRow(_prm.RiserSw),
            BuildPrmDataRow(_prm.RiserMode),
            BuildPrmDataRow(_prm.RiserCtrl),
            BuildPrmDataRow(_prm.RiserBeat),
            BuildPrmDataRow(_prm.RiserShape),
            BuildPrmDataRow(_prm.RiserReso),
            BuildPrmDataRow(_prm.RiserLevel),
        }));
        var effectsCard = BuildEffectsCard();
        var voiceCard   = BuildVoiceCard();
        Grid.SetRow(riserCard,   0); colB.Children.Add(riserCard);
        Grid.SetRow(effectsCard, 1); colB.Children.Add(effectsCard);
        Grid.SetRow(voiceCard,   2); colB.Children.Add(voiceCard);
        Grid.SetColumn(colB, 1); Grid.SetRow(colB, 0);
        PrmViewerGrid.Children.Add(colB);

        // ── Row 1 — OSC DRAW (col 0) + OSC CHOP (col 1), guaranteed same height ─
        var drawCard = BuildOscDrawCard();
        var chopCard = BuildOscChopCard();
        Grid.SetColumn(drawCard, 0); Grid.SetRow(drawCard, 1);
        Grid.SetColumn(chopCard, 1); Grid.SetRow(chopCard, 1);
        PrmViewerGrid.Children.Add(drawCard);
        PrmViewerGrid.Children.Add(chopCard);

        // ── Row 2 — full-width SEQUENCER card ────────────────────────────
        var seqCard = BuildSequencerCard();
        Grid.SetColumn(seqCard, 0); Grid.SetRow(seqCard, 2); Grid.SetColumnSpan(seqCard, 2);
        PrmViewerGrid.Children.Add(seqCard);
    }

    // CC-mapped parameter → live data row (subscribes to snapshot changes).
    private Control BuildCcDataRow(S1Parameter param)
    {
        var lbl = new TextBlock { Text = GetPrmViewerCcValue(param, SnapshotValue(param)) };
        _prm.CcSnapshotChanged += (_, _) => Dispatcher.UIThread.Post(
            () => lbl.Text = GetPrmViewerCcValue(param, SnapshotValue(param)));
        return Dashboard.BuildDataRow(param.Name, lbl);
    }

    // LFO Rate has two distinct displays: a 1-31 sync-slot label when LFO_SYNC=1,
    // or a 0-255 free-run value otherwise. PrmFileManager already stores the
    // resolved CC value (0-30 sync index, or 0-127 free), so this row just picks
    // the formatting based on the snapshot's LFO_SYNC value.
    private Control BuildLfoRateViewerRow(S1Parameter param)
    {
        var lbl = new TextBlock();
        string Format()
        {
            int cc       = SnapshotValue(param);
            int syncMode = _prm.CcSnapshot.TryGetValue(106, out var s) ? s : 0;
            if (syncMode != 0)
            {
                int idx = Math.Clamp(cc, 0, CcDisplay.LfoSyncLabels.Length - 1);
                return CcDisplay.LfoSyncLabels[idx];
            }
            return ((int)Math.Round(cc * 255.0 / 127)).ToString();
        }
        lbl.Text = Format();
        _prm.CcSnapshotChanged += (_, _) => Dispatcher.UIThread.Post(
            () => lbl.Text = Format());
        return Dashboard.BuildDataRow(param.Name, lbl);
    }

    // PRM-only parameter → live data row.
    private Control BuildPrmDataRow(PrmParameter p)
    {
        var lbl = new TextBlock { Text = CcDisplay.PrmValue(p) };
        p.ValueChanged += (_, _) => Dispatcher.UIThread.Post(
            () => lbl.Text = CcDisplay.PrmValue(p));
        return Dashboard.BuildDataRow(p.Name, lbl);
    }

    // ── OSC card composition ─────────────────────────────────────────────────

    private Border BuildOscillatorCard()
    {
        // Explicit row-major fill of the 3-col grid produces the requested column layout:
        // Col 1 = Square/Saw/Sub/Noise · Col 2 = Range/PW/PWM Src/Sub Oct · Col 3 = LFO Pitch/Noise/Fine/Bend.
        int[] order = { 19, 14, 13,    20, 15, 78,    21, 16, 76,    23, 22, 18 };
        var rows = order.Select(cc => (Control)BuildCcDataRow(RequireCC(cc)));
        return Dashboard.BuildPrmCard("OSCILLATOR", OscAccent, Dashboard.BuildThreeColGrid(rows));
    }

    private Border BuildOscDrawCard()
    {
        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                MakeDrawBarsControl(),
                BuildCcDataRow(RequireCC(102)),  // Multiply
                BuildCcDataRow(RequireCC(107)),  // Step/Slope
            },
        };
        return Dashboard.BuildPrmCard("OSC DRAW", OscAccent, body);
    }

    private Border BuildOscChopCard()
    {
        // PRM-level "Type" / "Comb Type" aren't surfaced as displayable parameters in the
        // current data model, so this card omits them and keeps the canonical CC mapping.
        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                MakeChopPatternControl(),
                BuildCcDataRow(RequireCC(103)),  // Overtone
                BuildCcDataRow(RequireCC(104)),  // Comb
            },
        };
        return Dashboard.BuildPrmCard("OSC CHOP", OscAccent, body);
    }

    // ── Effects card composition (3 sub-columns) ─────────────────────────────

    private Border BuildEffectsCard()
    {
        var revCol = Dashboard.BuildEffectsSubCol("REVERB", FxAccent, new[]
        {
            BuildCcDataRow(RequireCC(91)),                 // Level
            BuildCcDataRow(RequireCC(89)),                 // Time
            BuildPrmDataRow(_prm.ReverbMain[0]),           // Type
            BuildPrmDataRow(_prm.ReverbAdv[0]),            // Pre-Delay
            BuildPrmDataRow(_prm.ReverbAdv[1]),            // Density
        });

        var delItems = new List<Control>
        {
            BuildCcDataRow(RequireCC(92)),                 // Level
            BuildEffectsDelayTimeRow(),                    // Time (context-aware ms or tempo)
            BuildPrmDataRow(_prm.DelayMain[0]),            // Sync (DELAY_SW)
            BuildPrmDataRow(_prm.DelayTempo),              // Tempo
            BuildPrmDataRow(_prm.DelayAdv[0]),             // Feedback
        };
        var delCol = Dashboard.BuildEffectsSubCol("DELAY", FxAccent, delItems);

        var chorusCol = Dashboard.BuildEffectsSubCol("CHORUS", FxAccent, new[]
        {
            BuildCcDataRow(RequireCC(93)),                 // Type
        });

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing     = 14,
        };
        Grid.SetColumn(revCol,    0); grid.Children.Add(revCol);
        Grid.SetColumn(delCol,    1); grid.Children.Add(delCol);
        Grid.SetColumn(chorusCol, 2); grid.Children.Add(chorusCol);

        return Dashboard.BuildPrmCard("EFFECTS", FxAccent, grid);
    }

    // Like MakeDelayTimeViewerRow but using the new dashboard row chrome.
    private Control BuildEffectsDelayTimeRow()
    {
        var delaySw     = _prm.DelayMain[0];
        var delayTimeCC = RequireCC(90);

        var lbl = new TextBlock();
        void Refresh()
        {
            int delayTimeVal = SnapshotValue(delayTimeCC);
            lbl.Text = delaySw.Value == 0
                ? $"{1 + (int)Math.Round(delayTimeVal * 739.0 / 127)}ms"
                : CcDisplay.PrmValue(_prm.DelayTempo);
        }
        Refresh();
        delaySw.ValueChanged         += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _prm.CcSnapshotChanged       += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _prm.DelayTempo.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);

        return Dashboard.BuildDataRow("Time", lbl);
    }

    // ── Voice card composition (main grid + CHORD subgrid) ──────────────────

    private Border BuildVoiceCard()
    {
        // Row-major fill of a 3-col grid (last cell empty since we have 8 params):
        // Col 1 = Polyphony/Portamento/Glide · Col 2 = Mod Wheel/Exp/Damper · Col 3 = Transpose/Pan.
        int[] mainOrder = { 80, 1, 77,    31, 11, 10,    5, 64 };
        var mainItems = mainOrder
            .Select(cc => (Control)BuildCcDataRow(RequireCC(cc)))
            .ToList();

        var chordItems = new List<Control>
        {
            BuildCcDataRow(RequireCC(81)),  // V2
            BuildCcDataRow(RequireCC(82)),  // V3
            BuildCcDataRow(RequireCC(83)),  // V4
            BuildCcDataRow(RequireCC(85)),  // V2 Shift
            BuildCcDataRow(RequireCC(86)),  // V3 Shift
            BuildCcDataRow(RequireCC(87)),  // V4 Shift
        };

        var body = new StackPanel
        {
            Children =
            {
                Dashboard.BuildThreeColGrid(mainItems),
                Dashboard.BuildSubHeader("CHORD", VoiceAccent),
                Dashboard.BuildThreeColGrid(chordItems),
            },
        };
        return Dashboard.BuildPrmCard("VOICE", VoiceAccent, body);
    }

    // ── Sequencer card composition (PATTERN / ARP / MOTION / D-MOTION) ──────

    private Border BuildSequencerCard()
    {
        var seqAccentClr = ((ISolidColorBrush)SeqAccent).Color;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto,*"),  // pattern, arp, motion, divider, d-motion
            ColumnSpacing     = 14,
        };

        var patCol = new StackPanel { Spacing = 1 };
        patCol.Children.Add(Dashboard.BuildSubHeader("PATTERN", SeqAccent));
        patCol.Children.Add(Dashboard.BuildDataRow("Tempo", _tempoLabel));
        patCol.Children.Add(BuildPrmDataRow(_prm.Leng));
        patCol.Children.Add(BuildPrmDataRow(_prm.Shuffle));
        patCol.Children.Add(BuildPrmDataRow(_prm.Level));
        Grid.SetColumn(patCol, 0); grid.Children.Add(patCol);

        var arpCol = new StackPanel { Spacing = 1 };
        arpCol.Children.Add(Dashboard.BuildSubHeader("ARPEGGIATOR", SeqAccent));
        arpCol.Children.Add(BuildPrmDataRow(_prm.ArpType));
        arpCol.Children.Add(BuildPrmDataRow(_prm.ArpRate));
        Grid.SetColumn(arpCol, 1); grid.Children.Add(arpCol);

        var motCol = new StackPanel { Spacing = 1 };
        motCol.Children.Add(Dashboard.BuildSubHeader("AUTOMATION", SeqAccent));
        var laneGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing     = 6,
        };
        for (int i = 0; i < 8; i++)
        {
            var row = Dashboard.BuildDataRow($"Lane {i + 1}", _motionCcLabels[i]);
            Grid.SetRow(row, i / 2);
            Grid.SetColumn(row, i % 2);
            if (laneGrid.RowDefinitions.Count <= i / 2)
                laneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            laneGrid.Children.Add(row);
        }
        motCol.Children.Add(laneGrid);
        Grid.SetColumn(motCol, 2); grid.Children.Add(motCol);

        // Left divider rule before D-MOTION.
        var divider = new Border
        {
            Width      = 1,
            Background = s_dashCardBorder,
            Margin     = new Thickness(4, 18, 4, 4),
        };
        Grid.SetColumn(divider, 3); grid.Children.Add(divider);

        var dmCol = new StackPanel { Spacing = 1 };
        dmCol.Children.Add(Dashboard.BuildSubHeader("D-MOTION", PrmDmAccent));
        dmCol.Children.Add(BuildPrmDataRow(_prm.DmAssignX));
        dmCol.Children.Add(BuildPrmDataRow(_prm.DmAssignY));
        Grid.SetColumn(dmCol, 4); grid.Children.Add(dmCol);

        // Header with right-aligned VIEW STEPS → button.
        var viewStepsLabel = new TextBlock
        {
            Text          = "VIEW STEPS  →",
            FontSize      = 9.5,
            FontWeight    = FontWeight.SemiBold,
            LetterSpacing = 0.8,
            Foreground    = SeqAccent,
        };
        var viewStepsBtn = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush     = SeqAccent,
            Background      = new SolidColorBrush(Color.FromArgb(0x18, seqAccentClr.R, seqAccentClr.G, seqAccentClr.B)),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(8, 3),
            Cursor          = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            Child           = viewStepsLabel,
        };

        void RefreshViewBtn()
        {
            bool has = HasSequencerContent();
            viewStepsBtn.IsEnabled = has;
            viewStepsBtn.Opacity   = has ? 1.0 : 0.35;
        }
        RefreshViewBtn();
        _prm.Sequence.DataChanged += (_, _) => Dispatcher.UIThread.Post(RefreshViewBtn);
        viewStepsBtn.PointerPressed += (_, _) => ShowSequencerWindow();

        // Header: dot + SEQUENCER + spacer + VIEW STEPS button
        var seqAccentDot = new Ellipse
        {
            Width = 6, Height = 6, Fill = SeqAccent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var seqTitle = new TextBlock
        {
            Text              = "SEQUENCER",
            FontSize          = 8.5,
            FontWeight        = FontWeight.Bold,
            LetterSpacing     = 1.5,
            Foreground        = SeqAccent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0, 0, 0),
        };
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal,
                                          Children = { seqAccentDot, seqTitle } };
        Grid.SetColumn(headerLeft, 0);   headerGrid.Children.Add(headerLeft);
        Grid.SetColumn(viewStepsBtn, 2); headerGrid.Children.Add(viewStepsBtn);

        var headerUnderline = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 8),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x60, seqAccentClr.R, seqAccentClr.G, seqAccentClr.B), 0),
                    new GradientStop(Color.FromArgb(0x00, seqAccentClr.R, seqAccentClr.G, seqAccentClr.B), 1),
                },
            },
        };

        var body = new StackPanel { Children = { headerGrid, headerUnderline, grid } };
        return new Border
        {
            Background      = Palette.BgCard,
            BorderBrush     = Palette.BdCard,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(8, 6),
            Child           = body,
        };
    }


    private int SnapshotValue(S1Parameter param) =>
        _prm.CcSnapshot.TryGetValue(param.CcNumber, out var v) ? v : param.Value;

    private static string GetPrmViewerCcValue(S1Parameter param, int value)
    {
        if (param.Options is not null)
        {
            int idx = Math.Clamp(value, 0, param.Options.Length - 1);
            return param.Options[idx];
        }
        return param.ParameterType switch
        {
            S1ParameterType.Toggle        => value > 0 ? "On" : "Off",
            S1ParameterType.BipolarSlider => CcDisplay.FormatSemitone(Math.Clamp(value - 64, -12, 12)),
            _                             => CcDisplay.KnobValue(param, value),
        };
    }


    // 16-bar bipolar draw waveform display (lo byte first, then hi byte per PRM value).
    // Visual style: dashed centerline, opacity scales with magnitude, signed numeric value below.
    private Panel MakeDrawBarsControl()
    {
        const double BarH = 140.0;    // total visualization height (matches OSC CHOP grid)
        const double Half = BarH / 2.0;

        var posBars   = new Border[16];
        var negBars   = new Border[16];
        var valLabels = new TextBlock[16];

        void UpdateBars()
        {
            for (int pt = 0; pt < DrawWave.Points; pt++)
            {
                int signed = _prm.DrawWave.GetPoint(pt);
                int raw    = signed < 0 ? signed + 65536 : signed;
                int lo     = raw & 0xFF;
                int hi     = (raw >> 8) & 0xFF;
                int[] pads = { lo > 127 ? lo - 256 : lo, hi > 127 ? hi - 256 : hi };

                for (int b = 0; b < 2; b++)
                {
                    int    idx  = pt * 2 + b;
                    int    v    = pads[b];
                    double norm = Math.Clamp(v / 100.0, -1.0, 1.0);
                    double mag  = Math.Abs(norm);
                    double h    = Math.Max(1.0, mag * (Half - 2));

                    posBars[idx].Height  = v > 0 ? h : 0;
                    posBars[idx].Opacity = 0.5 + mag * 0.5;
                    negBars[idx].Height  = v < 0 ? h : 0;
                    negBars[idx].Opacity = 0.5 + mag * 0.5;
                    valLabels[idx].Text  = v > 0 ? $"+{v}" : v.ToString();
                }
            }
        }

        // Bar row: a Grid that lays each bar in its own column over a dashed centerline.
        var barsCanvas = new Grid
        {
            Height = BarH,
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", 16))),
        };

        // Dashed centerline spanning all columns.
        var centerline = new Rectangle
        {
            Height          = 1,
            Fill            = Brushes.Transparent,
            Stroke          = new SolidColorBrush(Color.Parse("#2A2A33")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 2 },
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumnSpan(centerline, 16);
        barsCanvas.Children.Add(centerline);

        for (int i = 0; i < 16; i++)
        {
            // Each column hosts one stacked Grid: top half = pos bar grows up, bottom half = neg bar grows down.
            var colGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,*"),
                Margin         = new Thickness(1, 0),
            };

            var posBar = new Border
            {
                Background          = OscAccent,
                Width               = double.NaN,
                Height              = 0,
                VerticalAlignment   = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius        = new CornerRadius(1, 1, 0, 0),
                Margin              = new Thickness(0, 0, 0, 1),  // sit just above centerline
            };
            var negBar = new Border
            {
                Background          = OscAccent,
                Width               = double.NaN,
                Height              = 0,
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius        = new CornerRadius(0, 0, 1, 1),
                Margin              = new Thickness(0, 1, 0, 0),
            };
            posBars[i] = posBar;
            negBars[i] = negBar;

            Grid.SetRow(posBar, 0); colGrid.Children.Add(posBar);
            Grid.SetRow(negBar, 1); colGrid.Children.Add(negBar);

            Grid.SetColumn(colGrid, i);
            barsCanvas.Children.Add(colGrid);
        }

        // Container border with #0E0E12 background.
        var barsBorder = new Border
        {
            Background   = s_chopVizBg,
            CornerRadius = new CornerRadius(2),
            Padding      = new Thickness(2, 0),
            Child        = barsCanvas,
        };

        // Label row: 16 columns aligned with the bars, signed numeric values.
        var labelsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", 16))),
            Margin            = new Thickness(2, 2, 2, 0),
        };
        for (int i = 0; i < 16; i++)
        {
            var lbl = new TextBlock
            {
                FontSize      = 7.5,
                FontFamily    = s_dashMonoFont,
                Foreground    = s_dashLabelBrush,
                Text          = "0",
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            valLabels[i] = lbl;
            Grid.SetColumn(lbl, i);
            labelsGrid.Children.Add(lbl);
        }

        UpdateBars();
        _prm.DrawWave.PointsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateBars);

        return new StackPanel { Children = { barsBorder, labelsGrid } };
    }

    // OSC CHOP — 16×4 LED matrix with waveform row labels.
    // Cells are small rounded rectangles (crisp, no AA blur from circles or glow).
    private Control MakeChopPatternControl()
    {
        var labelColumn = new Grid
        {
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", ChopPattern.Waveforms))),
            Margin         = new Thickness(0, 0, 6, 0),
        };
        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            var lbl = new TextBlock
            {
                Text                = ChopPattern.WaveformNames[w],
                FontSize            = 9.5,
                Foreground          = s_dashLabelBrush,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(lbl, w);
            labelColumn.Children.Add(lbl);
        }

        var ledGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", ChopPattern.Steps))),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", ChopPattern.Waveforms))),
            ColumnSpacing     = 3,
            RowSpacing        = 3,
            Height            = 140,
        };

        var cells = new Border[ChopPattern.Waveforms, ChopPattern.Steps];
        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            for (int s = 0; s < ChopPattern.Steps; s++)
            {
                bool on = _prm.ChopPattern.GetStep(w, s);
                var cell = new Border
                {
                    Background          = on ? OscAccent : s_chopLedOff,
                    CornerRadius        = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    MinWidth            = 8,
                };
                cells[w, s] = cell;
                Grid.SetRow(cell, w);
                Grid.SetColumn(cell, s);
                ledGrid.Children.Add(cell);
            }
        }

        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            int waveform = w;
            _prm.ChopPattern.PatternChanged += (_, changedWaveform) =>
            {
                if (changedWaveform != waveform) return;
                Dispatcher.UIThread.Post(() =>
                {
                    for (int s = 0; s < ChopPattern.Steps; s++)
                    {
                        bool on = _prm.ChopPattern.GetStep(waveform, s);
                        cells[waveform, s].Background = on ? OscAccent : s_chopLedOff;
                    }
                });
            };
        }

        var ledArea = new Border
        {
            Background   = s_chopVizBg,
            CornerRadius = new CornerRadius(2),
            Padding      = new Thickness(3),
            Child        = ledGrid,
        };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(labelColumn, 0); layout.Children.Add(labelColumn);
        Grid.SetColumn(ledArea,     1); layout.Children.Add(ledArea);
        return layout;
    }

}
