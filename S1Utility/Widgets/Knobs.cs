using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Controls;
using S1Utility.Core;

namespace S1Utility.Widgets;

// Static factories for the standard parameter widgets used by Tab 1 panels.
// Each takes its S1Patch dependency explicitly so they have no `this` capture
// of MainWindow's private state. MarkSynced is gated on patch.IsConnected so
// user drags while offline don't pretend the synth caught up.
internal static class Knobs
{
    // ── Cached brushes (avoid per-event allocations in Refresh() closures) ────

    private static readonly IBrush s_ledCellOn       = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
    private static readonly IBrush s_ledCellOff      = Palette.BgInset;
    private static readonly IBrush s_ledBorderOff    = new SolidColorBrush(Color.Parse("#23232C"));
    private static readonly IBrush s_ledTextOff      = Palette.FgOff;
    private static readonly IBrush s_ledBorderUnsync = new SolidColorBrush(Color.Parse("#252525"));
    private static readonly IBrush s_ledTextUnsync   = new SolidColorBrush(Color.Parse("#2A2A2A"));

    private static readonly IBrush s_chordToggleOn     = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
    private static readonly IBrush s_chordToggleOff    = Palette.BgInset;
    private static readonly IBrush s_chordBorderOff    = Palette.BdInset;
    private static readonly IBrush s_chordTextOff      = Palette.FgOff;
    private static readonly IBrush s_chordBorderUnsync = new SolidColorBrush(Color.Parse("#252525"));
    private static readonly IBrush s_chordTextUnsync   = new SolidColorBrush(Color.Parse("#333333"));


    // ── Standard rotary knob with value/label rows ────────────────────────────

    public static Control MakeKnob(
        S1Patch patch,
        S1Parameter param,
        IBrush accent,
        int minCcValue = 0,
        double containerWidth = 68)
    {
        string initDisplay = CcDisplay.KnobValue(param);

        var knob = new RotaryKnob
        {
            Value       = param.Value,
            AccentBrush = accent,
            IsSynced    = param.IsSynced,
            MinValue    = minCcValue,
        };
        ToolTip.SetTip(knob, $"{param.Name}: {initDisplay}");

        var valueLabel = new TextBlock
        {
            Classes = { "param-value-label" },
            Text    = param.IsSynced ? initDisplay : "?",
        };

        // Guard: true while param.ValueChanged is pushing a value to the knob so
        // knob.ValueChanged does not treat the programmatic update as a user drag.
        bool updatingFromModel = false;

        knob.ValueChanged += (_, v) =>
        {
            if (updatingFromModel) return;
            param.Value = Math.Max(minCcValue, v);
            if (patch.IsConnected) param.MarkSynced();
            string display = CcDisplay.KnobValue(param);
            ToolTip.SetTip(knob, $"{param.Name}: {display}");
            valueLabel.Text = display;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                updatingFromModel = true;
                knob.Value = v;
                updatingFromModel = false;
                string display = CcDisplay.KnobValue(param);
                ToolTip.SetTip(knob, $"{param.Name}: {display}");
                valueLabel.Text = param.IsSynced ? display : "?";
            });

        param.SyncStateChanged += (_, synced) =>
            Dispatcher.UIThread.Post(() =>
            {
                knob.IsSynced = synced;
                string display = CcDisplay.KnobValue(param);
                valueLabel.Text = synced ? display : "?";
            });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        return Dashboard.MakeKnobContainer(knob, valueLabel, nameLabel, containerWidth);
    }

    // ── LED segmented button group (replaces ComboBox / CheckBox in Tab 1) ────

    public static Control MakeLedButtonGroup(S1Patch patch, S1Parameter param, IBrush accent)
    {
        string[] opts = param.Options
            ?? (param.ParameterType == S1ParameterType.Toggle ? new[] { "Off", "On" } : new[] { "0", "1" });

        bool waveIcons = param.CcNumber == 12;

        var borders  = new Border[opts.Length];
        var setColor = new Action<IBrush>[opts.Length];

        bool isSynced = param.IsSynced;

        int GetIndex(int v) => param.ParameterType == S1ParameterType.Toggle
            ? (v > 0 ? 1 : 0)
            : Math.Clamp(v, 0, opts.Length - 1);

        void Refresh(int val)
        {
            int active = GetIndex(val);
            for (int i = 0; i < borders.Length; i++)
            {
                bool on = i == active;
                if (isSynced)
                {
                    borders[i].Background  = on ? s_ledCellOn  : s_ledCellOff;
                    borders[i].BorderBrush = on ? accent       : s_ledBorderOff;
                    setColor[i](on ? accent : s_ledTextOff);
                }
                else
                {
                    borders[i].Background  = s_ledCellOff;
                    borders[i].BorderBrush = s_ledBorderUnsync;
                    setColor[i](s_ledTextUnsync);
                }
            }
        }

        var row = new UniformGrid { Rows = 1 };

        for (int i = 0; i < opts.Length; i++)
        {
            int idx = i;

            Control content;
            if (waveIcons)
            {
                var poly = new Polyline
                {
                    StrokeThickness = 1.3,
                    StrokeLineCap   = PenLineCap.Round,
                    Points          = new AvaloniaList<Point>(WaveformIconPoints(i)),
                };
                var wc = new Canvas { Width = 26, Height = 12 };
                wc.Children.Add(poly);
                content = wc;
                setColor[i] = brush => poly.Stroke = brush;
            }
            else
            {
                var (display, tooltip) = ShortLabel(opts[i]);
                var lbl = new TextBlock { Text = display, FontSize = 10 };
                content = lbl;
                setColor[i] = brush => lbl.Foreground = brush;
                if (tooltip is not null) ToolTip.SetTip(lbl, tooltip);
            }

            var cell = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Padding         = waveIcons ? new Thickness(5, 4) : new Thickness(8, 3),
                Margin          = new Thickness(1),
                Height          = 22,
                Cursor          = new Cursor(StandardCursorType.Hand),
                Child           = content,
            };
            cell.PointerPressed += (_, _) =>
            {
                if (patch.IsConnected) param.MarkSynced();
                param.Value = param.ParameterType == S1ParameterType.Toggle ? (idx > 0 ? 127 : 0) : idx;
            };
            borders[i] = cell;
            row.Children.Add(cell);
        }

        Refresh(param.Value);
        param.ValueChanged     += (_, v)      => Dispatcher.UIThread.Post(() => Refresh(v));
        param.SyncStateChanged += (_, synced) => Dispatcher.UIThread.Post(() =>
        {
            isSynced = synced;
            Refresh(param.Value);
        });

        return new StackPanel
        {
            Margin              = new Thickness(0, 2, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                row,
                new TextBlock
                {
                    Classes             = { "param-label" },
                    Text                = param.Name,
                    Margin              = new Thickness(0, 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
    }

    // Heterogeneous toggle-cell row — each cell can bind to a different param
    // (radio-style "set value X" or toggle-style "flip 0↔127"). Chrome matches
    // MakeLedButtonGroup exactly so it lines up with neighbouring LED strips.
    // Optional IsEnabled dims a cell at 0.4 opacity and ignores clicks while
    // preserving the underlying param value (e.g. NORMAL/FAST when Sync is on).
    // Observe forces the cell to re-refresh whenever a foreign param changes,
    // which is needed for IsEnabled predicates that read another param.
    public readonly record struct ToggleCell(
        string Label,
        S1Parameter Param,
        Func<bool> IsOn,
        Action OnClick,
        Func<bool>? IsEnabled = null,
        S1Parameter? Observe = null);

    public static Control MakeMixedToggleRow(S1Patch patch, IBrush accent, params ToggleCell[] cells)
    {
        var borders  = new Border[cells.Length];
        var labels   = new TextBlock[cells.Length];
        var synced   = new bool[cells.Length];

        var row = new UniformGrid { Rows = 1 };

        for (int i = 0; i < cells.Length; i++)
        {
            int idx     = i;
            var spec    = cells[i];
            synced[idx] = spec.Param.IsSynced;

            var lbl = new TextBlock
            {
                Text                = spec.Label,
                FontSize            = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var cell = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Padding         = new Thickness(8, 3),
                Margin          = new Thickness(1),
                Height          = 22,
                Cursor          = new Cursor(StandardCursorType.Hand),
                Child           = lbl,
            };
            cell.PointerPressed += (_, _) =>
            {
                if (spec.IsEnabled is not null && !spec.IsEnabled()) return;
                if (patch.IsConnected) spec.Param.MarkSynced();
                spec.OnClick();
            };

            borders[idx] = cell;
            labels[idx]  = lbl;

            void RefreshCell()
            {
                bool on      = spec.IsOn();
                bool enabled = spec.IsEnabled?.Invoke() ?? true;
                if (synced[idx])
                {
                    cell.Background  = on ? s_ledCellOn  : s_ledCellOff;
                    cell.BorderBrush = on ? accent       : s_ledBorderOff;
                    lbl.Foreground   = on ? accent       : s_ledTextOff;
                }
                else
                {
                    cell.Background  = s_ledCellOff;
                    cell.BorderBrush = s_ledBorderUnsync;
                    lbl.Foreground   = s_ledTextUnsync;
                }
                cell.Opacity = enabled ? 1.0 : 0.4;
            }
            RefreshCell();
            spec.Param.ValueChanged     += (_, _)      => Dispatcher.UIThread.Post(RefreshCell);
            spec.Param.SyncStateChanged += (_, s)      => Dispatcher.UIThread.Post(() => { synced[idx] = s; RefreshCell(); });
            if (spec.Observe is not null && !ReferenceEquals(spec.Observe, spec.Param))
                spec.Observe.ValueChanged += (_, _) => Dispatcher.UIThread.Post(RefreshCell);

            row.Children.Add(cell);
        }

        return new StackPanel
        {
            Margin              = new Thickness(0, 2, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children            = { row },
        };
    }

    // Short cell label for option strings that don't fit a uniform LED strip.
    // Returns (display, tooltip-or-null). Tooltip is the original full label.
    private static (string display, string? tooltip) ShortLabel(string option)
    {
        if (string.Equals(option, "Gate+Trig", System.StringComparison.OrdinalIgnoreCase))
            return ("G+T", "Gate+Trig");
        return (option.ToUpperInvariant(), null);
    }

    // Polyline point sets for the six LFO waveform icons (26×12 canvas).
    private static Point[] WaveformIconPoints(int index) => index switch
    {
        0 => new[] { new Point(0,11), new Point(13,1),  new Point(13,11), new Point(26,1)  },  // Sawtooth
        1 => new[] { new Point(0,1),  new Point(13,11), new Point(13,1),  new Point(26,11) },  // Inv Saw
        2 => new[] { new Point(0,6),  new Point(7,1),   new Point(19,11), new Point(26,6)  },  // Triangle
        3 => new[] { new Point(0,2),  new Point(13,2),  new Point(13,10), new Point(26,10) },  // Square
        4 => new[]                                                                              // Random (S&H)
        {
            new Point(0,3),  new Point(6,3),  new Point(6,9),  new Point(11,9),
            new Point(11,2), new Point(17,2), new Point(17,7), new Point(26,7),
        },
        _ => new[]                                                                              // Noise
        {
            new Point(0,6), new Point(3,2), new Point(6,10), new Point(9,4),
            new Point(12,9), new Point(15,3), new Point(18,11), new Point(21,2),
            new Point(24,7), new Point(26,5),
        },
    };

    // ── Chord voice row (LED toggle + semitone slider) ────────────────────────

    public static Control MakeChordVoiceRow(
        S1Patch patch, int n, S1Parameter toggleParam, S1Parameter shiftParam, IBrush accent)
    {
        int GetShift() => Math.Clamp(shiftParam.Value - 64, -12, 12);

        var lbl = new TextBlock
        {
            Text              = $"V{n}",
            Width             = 14,
            FontSize          = 8,
            Foreground        = Palette.FgMute,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggleLbl = new TextBlock
        {
            Text                = "ON",
            FontSize            = 7.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var toggle = new Border
        {
            Width             = 26,
            Height            = 14,
            BorderThickness   = new Thickness(1),
            CornerRadius      = new CornerRadius(2),
            Cursor            = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = toggleLbl,
        };

        bool toggleIsSynced = toggleParam.IsSynced;

        void RefreshToggle()
        {
            bool on = toggleParam.Value > 0;
            if (toggleIsSynced)
            {
                toggle.Background    = on ? s_chordToggleOn : s_chordToggleOff;
                toggle.BorderBrush   = on ? accent          : s_chordBorderOff;
                toggleLbl.Foreground = on ? accent          : s_chordTextOff;
            }
            else
            {
                toggle.Background    = s_chordToggleOff;
                toggle.BorderBrush   = s_chordBorderUnsync;
                toggleLbl.Foreground = s_chordTextUnsync;
            }
        }
        RefreshToggle();
        toggle.PointerPressed += (_, _) =>
        {
            if (patch.IsConnected) toggleParam.MarkSynced();
            toggleParam.Value = toggleParam.Value > 0 ? 0 : 127;
        };
        toggleParam.ValueChanged     += (_, _) => Dispatcher.UIThread.Post(RefreshToggle);
        toggleParam.SyncStateChanged += (_, synced) => Dispatcher.UIThread.Post(() =>
        {
            toggleIsSynced = synced;
            RefreshToggle();
        });

        var slider = new Slider
        {
            Minimum             = -12,
            Maximum             =  12,
            Value               = GetShift(),
            IsSnapToTickEnabled = true,
            TickFrequency       = 1,
            Width               = 88,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        var valLbl = new TextBlock
        {
            Text              = CcDisplay.FormatSemitone(GetShift()),
            FontSize          = 8,
            Foreground        = Palette.FgVal,
            Width             = 22,
            TextAlignment     = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void RefreshShiftSync(bool synced)
        {
            // Match the knob/LED treatment: gray out the slider+label when we
            // don't know the synth's value for this parameter.
            double op = synced ? 1.0 : 0.5;
            slider.Opacity = op;
            valLbl.Opacity = op;
        }
        RefreshShiftSync(shiftParam.IsSynced);

        slider.ValueChanged += (_, e) =>
        {
            int s = (int)Math.Round(e.NewValue);
            shiftParam.Value = s + 64;
            valLbl.Text = CcDisplay.FormatSemitone(s);
            if (patch.IsConnected) shiftParam.MarkSynced();
        };
        shiftParam.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() =>
        {
            int s = Math.Clamp(v - 64, -12, 12);
            slider.Value = s;
            valLbl.Text  = CcDisplay.FormatSemitone(s);
        });
        shiftParam.SyncStateChanged += (_, synced) =>
            Dispatcher.UIThread.Post(() => RefreshShiftSync(synced));

        return new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 4,
            Margin              = new Thickness(3, 2),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            = { lbl, toggle, slider, valLbl },
        };
    }

    // ── Drone latching button (sustain hold) ──────────────────────────────────

    public static Control MakeDroneButton(S1Patch patch, S1Parameter param, IBrush accent) =>
        MakeToggleButton(patch, param, accent, "HOLD", "Click to latch / release held notes");

    // Generic on/off toggle styled identically to a MakeMixedToggleRow cell —
    // text-only, accent tint when on, dim when unsynced.
    public static Control MakeToggleButton(S1Patch patch, S1Parameter param, IBrush accent, string labelText, string? tooltip = null)
    {
        var label = new TextBlock
        {
            Text                = labelText,
            FontSize            = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        var btn = new Border
        {
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(2),
            Padding             = new Thickness(8, 3),
            Margin              = new Thickness(1),
            Height              = 22,
            Cursor              = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child               = label,
        };
        if (tooltip is not null) ToolTip.SetTip(btn, tooltip);

        bool synced = param.IsSynced;

        void Refresh()
        {
            bool on = param.Value > 0;
            if (synced)
            {
                btn.Background   = on ? s_ledCellOn  : s_ledCellOff;
                btn.BorderBrush  = on ? accent       : s_ledBorderOff;
                label.Foreground = on ? accent       : s_ledTextOff;
            }
            else
            {
                btn.Background   = s_ledCellOff;
                btn.BorderBrush  = s_ledBorderUnsync;
                label.Foreground = s_ledTextUnsync;
            }
        }
        Refresh();
        btn.PointerPressed += (_, _) =>
        {
            if (patch.IsConnected) param.MarkSynced();
            param.Value = param.Value > 0 ? 0 : 127;
        };
        param.ValueChanged     += (_, _) => Dispatcher.UIThread.Post(Refresh);
        param.SyncStateChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            synced = s;
            Refresh();
        });

        return btn;
    }

    // ── LFO Rate knob: free 0-255 when sync off, 31 named values when sync on ──

    public static Control MakeLfoRateKnob(S1Patch patch, IBrush accent)
    {
        var param  = patch.GetByCC(3)   ?? throw new InvalidOperationException("CC 3 (LFO Rate) not found");
        var syncSw = patch.GetByCC(106) ?? throw new InvalidOperationException("CC 106 (LFO Sync) not found");

        string GetDisplay()
        {
            if (syncSw.Value == 0)
                return ((int)Math.Round(param.Value * 255.0 / 127)).ToString();
            int idx = Math.Clamp(param.Value, 0, CcDisplay.LfoSyncLabels.Length - 1);
            return CcDisplay.LfoSyncLabels[idx];
        }

        var knob       = new RotaryKnob { Value = param.Value, AccentBrush = accent, IsSynced = param.IsSynced };
        var valueLabel = new TextBlock  { Classes = { "param-value-label" }, Text = param.IsSynced ? GetDisplay() : "?" };
        ToolTip.SetTip(knob, $"{param.Name}: {GetDisplay()}");

        void Refresh()
        {
            string d = GetDisplay();
            ToolTip.SetTip(knob, $"{param.Name}: {d}");
            valueLabel.Text = param.IsSynced ? d : "?";
        }

        // When sync on, CC 0–N spread evenly across full knob rotation.
        int lfoSteps = CcDisplay.LfoSyncLabels.Length - 1;
        int SyncToKnob(int cc)   => (int)Math.Round(Math.Clamp(cc, 0, lfoSteps) * 127.0 / lfoSteps);
        int KnobToSync(int knob) => Math.Clamp((int)Math.Round(knob * (double)lfoSteps / 127), 0, lfoSteps);

        bool lfoUpdatingFromModel = false;

        knob.ValueChanged += (_, v) =>
        {
            if (lfoUpdatingFromModel) return;
            param.Value = syncSw.Value == 1 ? KnobToSync(v) : v;
            if (patch.IsConnected) param.MarkSynced();
            Refresh();
        };
        param.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() =>
        {
            lfoUpdatingFromModel = true;
            knob.Value = syncSw.Value == 1 ? SyncToKnob(v) : v;
            lfoUpdatingFromModel = false;
            Refresh();
        });
        syncSw.ValueChanged += (_, sw) => Dispatcher.UIThread.Post(() =>
        {
            lfoUpdatingFromModel = true;
            knob.Value = sw == 1 ? SyncToKnob(param.Value) : param.Value;
            lfoUpdatingFromModel = false;
            Refresh();
        });
        param.SyncStateChanged += (_, synced) => Dispatcher.UIThread.Post(() =>
        {
            knob.IsSynced = synced;
            Refresh();
        });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        return Dashboard.MakeKnobContainer(knob, valueLabel, nameLabel);
    }

    // ── Delay Time knob: ms when sync off, tempo-division when sync on ────────
    // DELAY_SW semantics on the device: 0 = Sync to Tempo, 1 = Off (free ms).
    // The knob keeps using CC90 in both modes; only the value mapping changes.

    public static Control MakeDelayTimeKnob(S1Patch patch, PrmParameter delaySw, PrmParameter delayTempo, IBrush accent)
    {
        var param = patch.GetByCC(90) ?? throw new InvalidOperationException("CC 90 (Delay Time) not found");

        string GetDisplay()
        {
            if (delaySw.Value == 1)
                return $"{DelayTimeMap.CcToMs(param.Value)}ms";
            var opts = delayTempo.Options!;
            int idx  = Math.Clamp(param.Value, 0, opts.Length - 1);
            return opts[idx];
        }

        var knob       = new RotaryKnob { Value = param.Value, AccentBrush = accent, IsSynced = param.IsSynced };
        var valueLabel = new TextBlock  { Classes = { "param-value-label" }, Text = param.IsSynced ? GetDisplay() : "?" };
        ToolTip.SetTip(knob, $"{param.Name}: {GetDisplay()}");

        void Refresh()
        {
            string d = GetDisplay();
            ToolTip.SetTip(knob, $"{param.Name}: {d}");
            valueLabel.Text = param.IsSynced ? d : "?";
        }

        int delaySteps = delayTempo.Options!.Length - 1;
        int SyncToKnob(int cc)   => (int)Math.Round(Math.Clamp(cc, 0, delaySteps) * 127.0 / delaySteps);
        int KnobToSync(int knob) => Math.Clamp((int)Math.Round(knob * (double)delaySteps / 127), 0, delaySteps);

        bool delayUpdatingFromModel = false;

        knob.ValueChanged += (_, v) =>
        {
            if (delayUpdatingFromModel) return;
            param.Value = delaySw.Value == 0 ? KnobToSync(v) : v;
            if (patch.IsConnected) param.MarkSynced();
            Refresh();
        };
        param.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() =>
        {
            delayUpdatingFromModel = true;
            knob.Value = delaySw.Value == 0 ? SyncToKnob(v) : v;
            delayUpdatingFromModel = false;
            Refresh();
        });
        delaySw.ValueChanged += (_, sw) => Dispatcher.UIThread.Post(() =>
        {
            delayUpdatingFromModel = true;
            knob.Value = sw == 0 ? SyncToKnob(param.Value) : param.Value;
            delayUpdatingFromModel = false;
            Refresh();
        });
        param.SyncStateChanged += (_, synced) => Dispatcher.UIThread.Post(() =>
        {
            knob.IsSynced = synced;
            Refresh();
        });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        return Dashboard.MakeKnobContainer(knob, valueLabel, nameLabel);
    }
}
