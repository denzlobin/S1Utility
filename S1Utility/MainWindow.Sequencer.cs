using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using S1Utility.Core;

namespace S1Utility;

public partial class MainWindow
{
    private static readonly IBrush s_seqGridBar  = new SolidColorBrush(Color.Parse("#484848"));
    private static readonly IBrush s_seqGridBeat = new SolidColorBrush(Color.Parse("#282828"));

    private static readonly bool[] s_isBlackKey =
        { false, true, false, true, false, false, true, false, true, false, true, false };

    // Voice colours reuse the section accents (OSC/Filter/Env/LFO) — each voice
    // gets a visually distinct hue from the same palette the rest of the editor uses.
    private static IBrush[] s_voiceColors => new[]
    {
        Palette.AccentOsc,    // V1 green
        Palette.AccentFilter, // V2 orange
        Palette.AccentEnv,    // V3 blue
        Palette.AccentLfo,    // V4 purple
    };

    private bool HasSequencerContent()
    {
        int count = _prm.Sequence.StepCount;
        for (int s = 0; s < count; s++)
            if (_prm.Sequence.Steps[s].HasContent()) return true;
        return false;
    }

    private void ShowSequencerWindow()
    {
        if (_seqWindow != null) { _seqWindow.Activate(); return; }

        var (seqControl, seqUnsubscribe) = MakeSequencerControl();

        const int LabelW = 26, ColW = 15, WPad = 56;
        int CalcWidth() => Math.Max(480, LabelW + _prm.Sequence.StepCount * ColW + WPad);

        _seqWindow = new Window
        {
            Title                 = "Steps & Automation",
            Width                 = CalcWidth(),
            SizeToContent         = SizeToContent.Height,
            MaxHeight             = 800,
            Background            = new SolidColorBrush(Color.Parse("#18181E")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new Border
            {
                Padding = new Thickness(14),
                Child   = seqControl,
            },
        };

        EventHandler dataHandler = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_seqWindow == null) return;
            _seqWindow.Width = CalcWidth();
        });
        _prm.Sequence.DataChanged += dataHandler;

        _seqWindow.Closed += (_, _) =>
        {
            _prm.Sequence.DataChanged -= dataHandler;
            seqUnsubscribe();
            _seqWindow = null;
        };

        _seqWindow.Show(this);
    }

    private async Task ExportMidiAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export MIDI",
            SuggestedFileName = _prm.LastLoadedSourceName ?? "sequencer",
            DefaultExtension  = ".mid",
            FileTypeChoices   = new[]
            {
                new FilePickerFileType("MIDI File") { Patterns = new[] { "*.mid", "*.midi" } },
            },
        });

        if (file == null) return;

        byte[] midiBytes = BuildMidiFile();
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(midiBytes);
    }

    private byte[] BuildMidiFile()
    {
        const int TicksPerBeat = 480;
        const int TicksPerStep = TicksPerBeat / 4;  // 16th note = 120 ticks

        double bpm       = _prm.Sequence.Tempo > 0 ? _prm.Sequence.Tempo / 100.0 : 120.0;
        int    usPerBeat = (int)Math.Round(60_000_000.0 / bpm);
        int    stepCount = _prm.Sequence.StepCount;

        var events = new List<(int Tick, byte[] Data)>();

        events.Add((0, new byte[]
        {
            0xFF, 0x51, 0x03,
            (byte)(usPerBeat >> 16), (byte)(usPerBeat >> 8), (byte)usPerBeat,
        }));

        for (int s = 0; s < stepCount; s++)
        {
            var step = _prm.Sequence.Steps[s];
            int tick = s * TicksPerStep;

            for (int v = 0; v < 4; v++)
            {
                int note = step.Notes[v];
                if (note < 0) continue;
                int vel     = step.Velocities[v] > 0 ? Math.Clamp(step.Velocities[v], 1, 127) : 100;
                int lenTick = (int)Math.Max(1, Math.Round(Math.Max(1, step.Lengths[v]) / 100.0 * TicksPerStep));
                events.Add((tick,            new byte[] { 0x90, (byte)note, (byte)vel }));
                events.Add((tick + lenTick,  new byte[] { 0x80, (byte)note, 0 }));
            }

            for (int t = 0; t < SequencerStep.Ticks; t++)
            {
                int subTick = tick + t * TicksPerStep / SequencerStep.Ticks;

                for (int m = 0; m < 8; m++)
                {
                    int mv = step.Motions[t, m];
                    if (mv < 0) continue;
                    int cc = _prm.Sequence.MotionCCs[m];
                    if (cc < 0) continue;
                    events.Add((subTick, new byte[] { 0xB0, (byte)cc, (byte)mv }));
                }

                if (step.PitchBends[t] != -32768)
                {
                    int pb = Math.Clamp(8192 + step.PitchBends[t], 0, 16383);
                    events.Add((subTick, new byte[] { 0xE0, (byte)(pb & 0x7F), (byte)((pb >> 7) & 0x7F) }));
                }
            }
        }

        int endTick = events.Count > 0 ? events.Max(e => e.Tick) + TicksPerStep : TicksPerStep;
        events.Add((endTick, new byte[] { 0xFF, 0x2F, 0x00 }));

        events.Sort((a, b) =>
        {
            int cmp = a.Tick.CompareTo(b.Tick);
            if (cmp != 0) return cmp;
            return (b.Data[0] == 0x80).CompareTo(a.Data[0] == 0x80);  // note-offs first
        });

        var track = new List<byte>();
        int prevTick = 0;
        foreach (var (tick, data) in events)
        {
            SmfWriteVlq(track, tick - prevTick);
            track.AddRange(data);
            prevTick = tick;
        }

        var smf = new List<byte>();
        smf.AddRange(new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d' });
        SmfWriteBe32(smf, 6);
        SmfWriteBe16(smf, 0);
        SmfWriteBe16(smf, 1);
        SmfWriteBe16(smf, TicksPerBeat);
        smf.AddRange(new byte[] { (byte)'M', (byte)'T', (byte)'r', (byte)'k' });
        SmfWriteBe32(smf, track.Count);
        smf.AddRange(track);

        return smf.ToArray();
    }

    private static void SmfWriteVlq(List<byte> buf, int value)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0) { stack.Push((byte)((value & 0x7F) | 0x80)); value >>= 7; }
        foreach (var b in stack) buf.Add(b);
    }

    private static void SmfWriteBe16(List<byte> buf, int value)
    {
        buf.Add((byte)(value >> 8));
        buf.Add((byte)value);
    }

    private static void SmfWriteBe32(List<byte> buf, int value)
    {
        buf.Add((byte)(value >> 24));
        buf.Add((byte)(value >> 16));
        buf.Add((byte)(value >> 8));
        buf.Add((byte)value);
    }

    // Adds vertical bar/beat grid lines to a sequencer canvas at every 4-step boundary.
    // Thicker line at every 16-step (bar) boundary; thinner at every 4-step (beat) boundary.
    private static readonly string[] s_noteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static string StepTooltip(int stepIdx, SequencerStep step)
    {
        var lines = new List<string> { $"Step {stepIdx + 1}" };
        for (int v = 0; v < 4; v++)
        {
            int n = step.Notes[v];
            if (n < 0) continue;
            lines.Add($"{s_noteNames[n % 12]}{n / 12 - 1}   vel {step.Velocities[v]}   gate {step.Lengths[v]}%");
        }
        lines.Add($"Prob P.{Math.Clamp(step.Probability, 0, 10) * 10}   Sub-step {step.SubStepPattern}");
        return string.Join("\n", lines);
    }

    // Filled area through a lane's motion samples, one point per sample at the
    // centre of its tick, joined by straight lines. Inactive samples (null) split
    // it into separate runs; each run is held flat to its first and last tick edge
    // so a lone sample still shows. Whether the S-1 itself interpolates between
    // samples is unverified; this is only a smoother rendering of the same points.
    private StreamGeometry BuildLaneArea(int stepCount, double labelW, double colW, double baseY,
                                         Func<SequencerStep, int, double?> sampleY)
    {
        double tickW = colW / SequencerStep.Ticks;
        var    geo   = new StreamGeometry();
        using var ctx = geo.Open();

        bool   open  = false;
        double lastX = 0, lastY = 0;

        void Close()
        {
            if (!open) return;
            ctx.LineTo(new Point(lastX + tickW / 2, lastY));
            ctx.LineTo(new Point(lastX + tickW / 2, baseY));
            ctx.EndFigure(true);
            open = false;
        }

        for (int s = 0; s < stepCount; s++)
        for (int t = 0; t < SequencerStep.Ticks; t++)
        {
            double? y = sampleY(_prm.Sequence.Steps[s], t);
            if (y is null) { Close(); continue; }

            double x = labelW + s * colW + (t + 0.5) * tickW;
            if (!open)
            {
                ctx.BeginFigure(new Point(x - tickW / 2, baseY), isFilled: true);
                ctx.LineTo(new Point(x - tickW / 2, y.Value));
                open = true;
            }
            ctx.LineTo(new Point(x, y.Value));
            lastX = x; lastY = y.Value;
        }
        Close();
        return geo;
    }

    // One transparent hover target per step on a lane; text returns null for steps with nothing to show.
    private static void AddLaneTooltips(Canvas lane, int stepCount, double labelW, double colW,
                                        double laneH, Func<int, string?> text)
    {
        for (int s = 0; s < stepCount; s++)
        {
            string? tip = text(s);
            if (tip == null) continue;
            var hit = new Border { Width = colW, Height = laneH, Background = Brushes.Transparent };
            ToolTip.SetTip(hit, tip);
            Canvas.SetLeft(hit, labelW + s * colW);
            Canvas.SetTop(hit,  0);
            lane.Children.Add(hit);
        }
    }

    // "Step N · LABEL" plus the step's 8 samples, "–" for an inactive one.
    private static string SamplesTooltip(int stepIdx, string label, Func<int, int?> sample) =>
        $"Step {stepIdx + 1} · {label}\n" + string.Join("  ",
            Enumerable.Range(0, SequencerStep.Ticks).Select(t => sample(t)?.ToString() ?? "–"));

    private static void AddSequencerGridLines(Canvas target, int stepCount, double labelW, double colW, double laneHeight)
    {
        for (int s = 0; s <= stepCount; s += 4)
        {
            var line = new Border
            {
                Width      = 1,
                Height     = laneHeight,
                Background = s % 16 == 0 ? s_seqGridBar : s_seqGridBeat,
            };
            Canvas.SetLeft(line, labelW + s * colW);
            Canvas.SetTop(line, 0);
            target.Children.Add(line);
        }
    }

    private (Control, Action) MakeSequencerControl()
    {
        const double BaseRowH =  5.0;
        const double BaseColW = 15.0;
        const double LabelW   = 26.0;
        const double StepNumH = 14.0;
        const double BaseLaneH = 20.0;

        // Cell size follows the window: dragging it larger stretches steps, rows
        // and automation lanes (a lane stays BaseLaneH / BaseRowH rows tall).
        double zoomColW = BaseColW, zoomRowH = BaseRowH;
        int    pitchRows = 1, laneCount = 0;

        var canvas      = new Canvas();
        var motionLanes = new StackPanel { Spacing = 2 };

        void Rebuild()
        {
            double ColW  = zoomColW, RowH = zoomRowH;
            double LaneH = BaseLaneH * RowH / BaseRowH;
            double TickW = ColW / SequencerStep.Ticks;  // one motion sample

            canvas.Children.Clear();
            motionLanes.Children.Clear();

            int count = _prm.Sequence.StepCount;

            // Auto-detect pitch range from active notes
            int minNote = 127, maxNote = 0;
            for (int s = 0; s < count; s++)
                foreach (var n in _prm.Sequence.Steps[s].Notes)
                    if (n >= 0) { minNote = Math.Min(minNote, n); maxNote = Math.Max(maxNote, n); }
            if (minNote > maxNote) { minNote = 48; maxNote = 72; }  // default C3–C5
            minNote = Math.Max(0,   minNote - 2);
            maxNote = Math.Min(127, maxNote + 2);

            int    pitchRange = maxNote - minNote + 1;
            pitchRows = pitchRange;
            double totalH     = pitchRange * RowH;
            double totalW     = LabelW + count * ColW;

            canvas.Width  = totalW;
            canvas.Height = totalH + StepNumH;

            // Black-key row shading
            for (int p = minNote; p <= maxNote; p++)
            {
                if (!s_isBlackKey[p % 12]) continue;
                var bg = new Border
                {
                    Width      = totalW - LabelW,
                    Height     = RowH,
                    Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
                };
                Canvas.SetLeft(bg, LabelW);
                Canvas.SetTop(bg,  (maxNote - p) * RowH);
                canvas.Children.Add(bg);
            }

            // Vertical grid lines: thick at bars (every 16), thin at beats (every 4)
            AddSequencerGridLines(canvas, count, LabelW, ColW, totalH);

            // Pitch labels (C notes only)
            for (int p = minNote; p <= maxNote; p++)
            {
                if (p % 12 != 0) continue;
                var lbl = new TextBlock
                {
                    Text       = $"C{p / 12 - 1}",
                    FontSize   = 7,
                    Foreground = new SolidColorBrush(Color.Parse("#606060")),
                };
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl,  (maxNote - p) * RowH - 1);
                canvas.Children.Add(lbl);
            }

            // Note blocks: one color per voice, opacity driven by velocity
            // (probability has its own lane). A sub-step splits the block into
            // equal parts; gate is not applied per part because how the S-1
            // combines the two is unverified.
            for (int s = 0; s < count; s++)
            {
                var step = _prm.Sequence.Steps[s];
                if (!step.Notes.Any(n => n >= 0)) continue;

                double x       = LabelW + s * ColW;
                string pattern = step.SubStepPattern;
                double partW   = ColW / pattern.Length;

                for (int v = 0; v < 4; v++)
                {
                    int note = step.Notes[v];
                    if (note < minNote || note > maxNote) continue;

                    double velAlpha = 0.25 + 0.75 * step.Velocities[v] / 127.0;
                    var    baseCol  = ((ISolidColorBrush)s_voiceColors[v]).Color;
                    var    fill     = new SolidColorBrush(Color.FromArgb(
                                          (byte)(255 * velAlpha),
                                          baseCol.R, baseCol.G, baseCol.B));

                    for (int p = 0; p < pattern.Length; p++)
                    {
                        if (pattern[p] != 'o') continue;
                        double w = pattern.Length == 1
                            ? Math.Max(1, ColW * Math.Max(1, step.Lengths[v]) / 100.0 - 1)
                            : Math.Max(1, partW - 1);

                        var rect = new Border
                        {
                            Width        = w,
                            Height       = RowH - 1,
                            Background   = fill,
                            CornerRadius = new CornerRadius(1),
                        };
                        Canvas.SetLeft(rect, x + p * partW);
                        Canvas.SetTop(rect,  (maxNote - note) * RowH);
                        canvas.Children.Add(rect);
                    }
                }

                // Hover target over the whole column: exact values the blocks only hint at.
                var hit = new Border { Width = ColW, Height = totalH, Background = Brushes.Transparent };
                ToolTip.SetTip(hit, StepTooltip(s, step));
                Canvas.SetLeft(hit, x);
                Canvas.SetTop(hit,  0);
                canvas.Children.Add(hit);
            }

            // Step numbers below (every 4 steps)
            for (int s = 0; s < count; s += 4)
            {
                var lbl = new TextBlock
                {
                    Text       = (s + 1).ToString(),
                    FontSize   = 8,
                    Foreground = new SolidColorBrush(Color.Parse("#484848")),
                };
                Canvas.SetLeft(lbl, LabelW + s * ColW + 1);
                Canvas.SetTop(lbl,  totalH + 2);
                canvas.Children.Add(lbl);
            }

            // Motion lanes ────────────────────────────────────────────────────
            var tempLanes = new List<Control>();

            for (int m = 0; m < 8; m++)
            {
                bool hasData = false;
                for (int s = 0; s < count; s++)
                    if (_prm.Sequence.Steps[s].HasMotion(m)) { hasData = true; break; }
                if (!hasData) continue;

                int    slot      = m;
                string laneLabel = _prm.Sequence.MotionCCs[m] >= 0
                    ? $"CC{_prm.Sequence.MotionCCs[m]}" : $"M{m + 1}";

                var lc   = new Canvas { Width = totalW, Height = LaneH };
                var lcBg = new Border { Width = totalW - LabelW, Height = LaneH,
                                        Background = new SolidColorBrush(Color.Parse("#1A1A1A")) };
                Canvas.SetLeft(lcBg, LabelW); Canvas.SetTop(lcBg, 0);
                lc.Children.Add(lcBg);

                var lcLbl = new TextBlock
                {
                    Text          = laneLabel,
                    FontSize      = 7,
                    Foreground    = new SolidColorBrush(Color.Parse("#707070")),
                    Width         = LabelW - 2,
                    TextAlignment = TextAlignment.Right,
                };
                Canvas.SetLeft(lcLbl, 0); Canvas.SetTop(lcLbl, LaneH / 2 - 5);
                lc.Children.Add(lcLbl);

                double baseY = LaneH - 1;
                lc.Children.Add(new Path
                {
                    Fill = s_voiceColors[slot % 4],
                    Data = BuildLaneArea(count, LabelW, ColW, baseY, (step, t) =>
                    {
                        int mv = step.Motions[t, slot];
                        return mv < 0 ? null : baseY - mv / 127.0 * (LaneH - 1);
                    }),
                });

                AddSequencerGridLines(lc, count, LabelW, ColW, LaneH);
                AddLaneTooltips(lc, count, LabelW, ColW, LaneH, s =>
                {
                    var step = _prm.Sequence.Steps[s];
                    return step.HasMotion(slot)
                        ? SamplesTooltip(s, laneLabel, t => step.Motions[t, slot] is int v && v >= 0 ? v : null)
                        : null;
                });

                tempLanes.Add(lc);
            }

            // Pitch-bend lane
            bool hasPb = false;
            for (int s = 0; s < count; s++)
                if (_prm.Sequence.Steps[s].HasPitchBend) { hasPb = true; break; }

            if (hasPb)
            {
                var lc   = new Canvas { Width = totalW, Height = LaneH };
                var lcBg = new Border { Width = totalW - LabelW, Height = LaneH,
                                        Background = new SolidColorBrush(Color.Parse("#1A1A1A")) };
                Canvas.SetLeft(lcBg, LabelW); Canvas.SetTop(lcBg, 0);
                lc.Children.Add(lcBg);

                var lcLbl = new TextBlock
                {
                    Text          = "PB",
                    FontSize      = 7,
                    Foreground    = new SolidColorBrush(Color.Parse("#707070")),
                    Width         = LabelW - 2,
                    TextAlignment = TextAlignment.Right,
                };
                Canvas.SetLeft(lcLbl, 0); Canvas.SetTop(lcLbl, LaneH / 2 - 5);
                lc.Children.Add(lcLbl);

                // Center line
                var cl = new Border { Width = totalW - LabelW, Height = 1,
                                       Background = new SolidColorBrush(Color.Parse("#303030")) };
                Canvas.SetLeft(cl, LabelW); Canvas.SetTop(cl, LaneH / 2);
                lc.Children.Add(cl);

                // One area around the centre line, drawn twice and clipped so the
                // part above centre is yellow and the part below is red.
                double mid  = LaneH / 2;
                var    area = BuildLaneArea(count, LabelW, ColW, mid, (step, t) =>
                {
                    int pb = step.PitchBends[t];
                    return pb == -32768 ? null : mid - Math.Clamp(pb / 8192.0, -1.0, 1.0) * (mid - 1);
                });
                lc.Children.Add(new Path
                {
                    Fill = new SolidColorBrush(Color.Parse("#E0C040")),
                    Data = area,
                    Clip = new RectangleGeometry(new Rect(0, 0, totalW, mid)),
                });
                lc.Children.Add(new Path
                {
                    Fill = new SolidColorBrush(Color.Parse("#C04040")),
                    Data = area,
                    Clip = new RectangleGeometry(new Rect(0, mid, totalW, mid)),
                });

                AddSequencerGridLines(lc, count, LabelW, ColW, LaneH);
                AddLaneTooltips(lc, count, LabelW, ColW, LaneH, s =>
                {
                    var step = _prm.Sequence.Steps[s];
                    return step.HasPitchBend
                        ? SamplesTooltip(s, "PB", t => step.PitchBends[t] is int v && v != -32768 ? v : null)
                        : null;
                });

                tempLanes.Add(lc);
            }

            // Probability lane: shown only when a note step is below P.100; bar height is the value.
            bool hasProb = false;
            for (int s = 0; s < count; s++)
            {
                var st = _prm.Sequence.Steps[s];
                if (st.Probability < 10 && st.Notes.Any(n => n >= 0)) { hasProb = true; break; }
            }

            if (hasProb)
            {
                var lc   = new Canvas { Width = totalW, Height = LaneH };
                var lcBg = new Border { Width = totalW - LabelW, Height = LaneH,
                                        Background = new SolidColorBrush(Color.Parse("#1A1A1A")) };
                Canvas.SetLeft(lcBg, LabelW); Canvas.SetTop(lcBg, 0);
                lc.Children.Add(lcBg);

                var lcLbl = new TextBlock
                {
                    Text          = "PROB",
                    FontSize      = 7,
                    Foreground    = new SolidColorBrush(Color.Parse("#707070")),
                    Width         = LabelW - 2,
                    TextAlignment = TextAlignment.Right,
                };
                Canvas.SetLeft(lcLbl, 0); Canvas.SetTop(lcLbl, LaneH / 2 - 5);
                lc.Children.Add(lcLbl);

                // P.100 guide, drawn under the bars like the PB centre line.
                var guide = new Border { Width = totalW - LabelW, Height = 1,
                                         Background = new SolidColorBrush(Color.Parse("#303030")) };
                Canvas.SetLeft(guide, LabelW); Canvas.SetTop(guide, 0);
                lc.Children.Add(guide);

                for (int s = 0; s < count; s++)
                {
                    var st = _prm.Sequence.Steps[s];
                    int prob = Math.Clamp(st.Probability, 0, 10);
                    if (prob == 0 || !st.Notes.Any(n => n >= 0)) continue;
                    double barH = prob / 10.0 * (LaneH - 1);
                    var    bar  = new Border
                    {
                        Width      = Math.Max(1, ColW - 2),
                        Height     = barH,
                        Background = Palette.AccentLfo,
                    };
                    Canvas.SetLeft(bar, LabelW + s * ColW + 1);
                    Canvas.SetTop(bar,  LaneH - 1 - barH);
                    lc.Children.Add(bar);
                }

                AddSequencerGridLines(lc, count, LabelW, ColW, LaneH);
                AddLaneTooltips(lc, count, LabelW, ColW, LaneH, s =>
                {
                    var step = _prm.Sequence.Steps[s];
                    return step.Notes.Any(n => n >= 0)
                        ? $"Step {s + 1} · P.{Math.Clamp(step.Probability, 0, 10) * 10}"
                        : null;
                });

                tempLanes.Add(lc);
            }

            laneCount = tempLanes.Count;
            if (tempLanes.Count > 0)
            {
                motionLanes.Children.Add(new TextBlock
                {
                    Text       = "AUTOMATION",
                    FontSize   = 8,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#606060")),
                    Margin     = new Thickness(0, 6, 0, 2),
                });
                foreach (var lc in tempLanes)
                    motionLanes.Children.Add(lc);
            }
        }

        EventHandler dataChangedHandler = (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
        _prm.Sequence.DataChanged += dataChangedHandler;

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content                       = new StackPanel { Children = { canvas, motionLanes } },
        };

        // Fit cells to the viewport. Whole pixels (floored) keep the content no larger
        // than the viewport, so scrollbars only appear below the base cell size.
        scroller.SizeChanged += (_, e) =>
        {
            int    count  = Math.Max(1, _prm.Sequence.StepCount);
            double colW   = Math.Max(BaseColW, Math.Floor((e.NewSize.Width - LabelW) / count));
            // Lane header and spacing don't scale; subtract them, then share the
            // rest between pitch rows and lanes (each lane = BaseLaneH / BaseRowH rows).
            double fixedH = motionLanes.Bounds.Height - laneCount * BaseLaneH * zoomRowH / BaseRowH;
            double rowH   = Math.Max(BaseRowH, Math.Floor(
                (e.NewSize.Height - StepNumH - fixedH) / (pitchRows + laneCount * BaseLaneH / BaseRowH)));
            if (colW == zoomColW && rowH == zoomRowH) return;
            zoomColW = colW;
            zoomRowH = rowH;
            Rebuild();
        };

        return (
            scroller,
            () => _prm.Sequence.DataChanged -= dataChangedHandler
        );
    }
}
