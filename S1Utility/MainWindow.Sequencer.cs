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

namespace S1Utility;

public partial class MainWindow
{
    private static readonly IBrush s_seqGridBar  = new SolidColorBrush(Color.Parse("#484848"));
    private static readonly IBrush s_seqGridBeat = new SolidColorBrush(Color.Parse("#282828"));

    private static readonly bool[] s_isBlackKey =
        { false, true, false, true, false, false, true, false, true, false, true, false };

    private static readonly IBrush[] s_voiceColors =
    {
        new SolidColorBrush(Color.Parse("#F0A040")),  // V1 orange
        new SolidColorBrush(Color.Parse("#40B0F0")),  // V2 blue
        new SolidColorBrush(Color.Parse("#70C870")),  // V3 green
        new SolidColorBrush(Color.Parse("#B070D8")),  // V4 purple
    };

    private bool HasSequencerContent()
    {
        int count = _prm.Sequence.StepCount;
        for (int s = 0; s < count; s++)
        {
            var step = _prm.Sequence.Steps[s];
            if (step.Notes.Any(n => n >= 0))   return true;
            if (step.Motions.Any(m => m >= 0)) return true;
            if (step.PitchBend != -32768)       return true;
        }
        return false;
    }

    private void ShowSequencerWindow()
    {
        if (_seqWindow != null) { _seqWindow.Activate(); return; }

        var (seqControl, seqUnsubscribe) = MakeSequencerControl();

        var seqAccentClr = ((SolidColorBrush)SeqAccent).Color;

        var exportLbl = new TextBlock
        {
            Text          = "EXPORT MIDI",
            FontSize      = 10.5,
            FontWeight    = FontWeight.SemiBold,
            LetterSpacing = 0.8,
        };
        var exportBtn = new Border
        {
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(4),
            Padding             = new Thickness(20, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 12, 0, 0),
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = exportLbl,
        };
        exportBtn.PointerPressed += async (_, _) => await ExportMidiAsync();

        void RefreshExportBtn()
        {
            bool has              = HasSequencerContent();
            exportBtn.IsEnabled   = has;
            exportBtn.Opacity     = has ? 1.0 : 0.35;
            exportBtn.BorderBrush = has ? SeqAccent : new SolidColorBrush(Color.Parse("#2E2E2E"));
            exportBtn.Background  = has
                ? new SolidColorBrush(Color.FromArgb(0x18, seqAccentClr.R, seqAccentClr.G, seqAccentClr.B))
                : new SolidColorBrush(Color.Parse("#161616"));
            exportLbl.Foreground  = has ? SeqAccent : new SolidColorBrush(Color.Parse("#555555"));
        }

        RefreshExportBtn();

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
                Child   = new StackPanel { Children = { seqControl, exportBtn } },
            },
        };

        EventHandler dataHandler = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_seqWindow == null) return;
            _seqWindow.Width = CalcWidth();
            RefreshExportBtn();
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
        if (_seqWindow == null) return;

        var file = await _seqWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export MIDI",
            SuggestedFileName = "sequencer",
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

            for (int m = 0; m < 8; m++)
            {
                int mv = step.Motions[m];
                if (mv < 0) continue;
                int cc = _prm.Sequence.MotionCCs[m];
                if (cc < 0) continue;
                events.Add((tick, new byte[] { 0xB0, (byte)cc, (byte)mv }));
            }

            if (step.PitchBend != -32768)
            {
                int pb     = Math.Clamp(8192 + (int)Math.Round(step.PitchBend * 8192.0 / 32768.0), 0, 16383);
                events.Add((tick, new byte[] { 0xE0, (byte)(pb & 0x7F), (byte)((pb >> 7) & 0x7F) }));
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
        const double RowH     =  5.0;
        const double ColW     = 15.0;
        const double LabelW   = 26.0;
        const double StepNumH = 14.0;
        const double LaneH    = 20.0;

        var infoLabel = new TextBlock
        {
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            Margin     = new Thickness(0, 0, 0, 6),
        };

        var canvas      = new Canvas();
        var motionLanes = new StackPanel { Spacing = 2 };

        void Rebuild()
        {
            canvas.Children.Clear();
            motionLanes.Children.Clear();

            int count = _prm.Sequence.StepCount;
            infoLabel.Text =
                $"Steps: {count}   " +
                $"Tempo: {_prm.Sequence.Tempo / 100.0:F1} BPM   " +
                $"Transpose: {_prm.Sequence.Transpose}   " +
                $"Shuffle: {_prm.Sequence.Shuffle}";

            // Auto-detect pitch range from active notes
            int minNote = 127, maxNote = 0;
            for (int s = 0; s < count; s++)
                foreach (var n in _prm.Sequence.Steps[s].Notes)
                    if (n >= 0) { minNote = Math.Min(minNote, n); maxNote = Math.Max(maxNote, n); }
            if (minNote > maxNote) { minNote = 48; maxNote = 72; }  // default C3–C5
            minNote = Math.Max(0,   minNote - 2);
            maxNote = Math.Min(127, maxNote + 2);

            int    pitchRange = maxNote - minNote + 1;
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

            // Note blocks — one color per voice, opacity driven by velocity
            for (int s = 0; s < count; s++)
            {
                var step = _prm.Sequence.Steps[s];
                double x = LabelW + s * ColW;

                for (int v = 0; v < 4; v++)
                {
                    int note = step.Notes[v];
                    if (note < minNote || note > maxNote) continue;

                    double noteW    = Math.Max(1, ColW * Math.Max(1, step.Lengths[v]) / 100.0 - 1);
                    double velAlpha = 0.25 + 0.75 * step.Velocities[v] / 127.0;
                    var    baseCol  = ((SolidColorBrush)s_voiceColors[v]).Color;

                    var rect = new Border
                    {
                        Width        = noteW,
                        Height       = RowH - 1,
                        Background   = new SolidColorBrush(Color.FromArgb(
                                           (byte)(255 * velAlpha),
                                           baseCol.R, baseCol.G, baseCol.B)),
                        CornerRadius = new CornerRadius(1),
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect,  (maxNote - note) * RowH);
                    canvas.Children.Add(rect);
                }
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
                    if (_prm.Sequence.Steps[s].Motions[m] != -1) { hasData = true; break; }
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

                for (int s = 0; s < count; s++)
                {
                    int mv = _prm.Sequence.Steps[s].Motions[slot];
                    if (mv < 0) continue;
                    double barH = Math.Max(1, mv / 127.0 * (LaneH - 1));
                    var bar = new Border
                    {
                        Width      = ColW - 2,
                        Height     = barH,
                        Background = s_voiceColors[slot % 4],
                    };
                    Canvas.SetLeft(bar, LabelW + s * ColW + 1);
                    Canvas.SetTop(bar,  LaneH - 1 - barH);
                    lc.Children.Add(bar);
                }

                AddSequencerGridLines(lc, count, LabelW, ColW, LaneH);

                tempLanes.Add(lc);
            }

            // Pitch-bend lane
            bool hasPb = false;
            for (int s = 0; s < count; s++)
                if (_prm.Sequence.Steps[s].PitchBend != -32768) { hasPb = true; break; }

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

                for (int s = 0; s < count; s++)
                {
                    int    pb   = _prm.Sequence.Steps[s].PitchBend;
                    if (pb == -32768) continue;
                    double norm = Math.Clamp(pb / 32768.0, -1.0, 1.0);
                    double barH = Math.Max(1, Math.Abs(norm) * (LaneH / 2 - 1));
                    var    bar  = new Border
                    {
                        Width      = ColW - 2,
                        Height     = barH,
                        Background = norm >= 0
                            ? new SolidColorBrush(Color.Parse("#E0C040"))
                            : new SolidColorBrush(Color.Parse("#C04040")),
                    };
                    Canvas.SetLeft(bar, LabelW + s * ColW + 1);
                    Canvas.SetTop(bar,  norm >= 0 ? LaneH / 2 - barH : LaneH / 2);
                    lc.Children.Add(bar);
                }

                AddSequencerGridLines(lc, count, LabelW, ColW, LaneH);

                tempLanes.Add(lc);
            }

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

        return (
            new StackPanel
            {
                Children =
                {
                    infoLabel,
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content                       = new StackPanel { Children = { canvas, motionLanes } },
                    },
                },
            },
            () => _prm.Sequence.DataChanged -= dataChangedHandler
        );
    }
}
