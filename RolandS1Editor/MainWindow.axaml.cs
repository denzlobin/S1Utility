using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Commons.Music.Midi;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using RolandS1Editor.Controls;

namespace RolandS1Editor;

public partial class MainWindow : Window
{
#pragma warning disable CS0618  // IMidiAccess is obsolete but IMidiAccess2 is not implemented by WinMM
    private readonly IMidiAccess _midi = MidiAccessManager.Default;
#pragma warning restore CS0618

    private readonly S1Patch _patch = new();

    // DryWetMidi is used for MIDI input — managed-midi's WinMM input callback
    // doesn't work on .NET 10 (MMSYSERR_INVALPARAM / error 11).
    private readonly List<InputDevice> _inputDevices = new();
    private InputDevice? _activeInput;
    private int MidiChannel => ChannelCombo.SelectedIndex >= 0 ? ChannelCombo.SelectedIndex + 1 : 3;

    // Non-null only while a Sync from Hardware session is active.
    private HardwareSyncSession? _syncSession;

    // Chop step-pattern model (4 waveforms × 16 steps, PRM-only).
    private readonly ChopPattern _chopPattern = new();

    // Brushes reused across all 64 step buttons.
    private static readonly IBrush s_chopOnBrush  = new SolidColorBrush(Color.Parse("#CC2222"));
    private static readonly IBrush s_chopOffBrush = new SolidColorBrush(Color.Parse("#383838"));
    private static readonly IBrush s_chopBorder   = new SolidColorBrush(Color.Parse("#505050"));

    // Raw key→value data from the last opened .PRM file — used for round-trip saves.
    private PrmFileData? _lastPrmData;

    // ── PRM-only effect parameters ────────────────────────────────────────────

    private static readonly string[] s_lowCutOpts = {
        "Flat","20","25","31.5","40","50","63","80","100","125",
        "160","200","250","315","400","500","630","800"
    };
    private static readonly string[] s_highCutOpts = {
        "630","800","1k","1.25k","1.6k","2k","2.5k","3.15k",
        "4k","5k","6.3k","8k","10k","12.5k","Flat"
    };

    // Layer 1 — main effect controls (visible by default)
    private static List<PrmParameter> BuildPrmDelayMain() => new()
    {
        new("Delay Sync", "DELAY_SW", options: new[] { "Off", "Sync to Tempo" }),
    };

    private static List<PrmParameter> BuildPrmReverbMain() => new()
    {
        new("Reverb Type", "REVERB_TYPE", options: new[] {
            "Ambience","Room","Hall 1","Hall 2","Plate","Spring","Modulate" }),
    };

    // Layer 2 — advanced effect controls (collapsed by default)
    private static List<PrmParameter> BuildPrmDelayAdv() => new()
    {
        new("Delay Feedback", "DELAY_FEEDBACK", prmMax: 255),
        new("Delay Low Cut",  "DELAY_LOW_CUT",  options: s_lowCutOpts),
        new("Delay High Cut", "DELAY_HIGH_CUT", options: s_highCutOpts),
    };

    private static List<PrmParameter> BuildPrmReverbAdv() => new()
    {
        new("Reverb Pre-Delay", "REVERB_PRE_DELAY", prmMax: 100),
        new("Reverb Density",   "REVERB_DENSITY",   prmMax: 10),
        new("Reverb Low Cut",   "REVERB_LOW_CUT",   options: s_lowCutOpts),
        new("Reverb High Cut",  "REVERB_HIGH_CUT",  options: s_highCutOpts),
    };

    private readonly List<PrmParameter> _prmDelayMain;
    private readonly List<PrmParameter> _prmReverbMain;
    private readonly List<PrmParameter> _prmDelayAdv;
    private readonly List<PrmParameter> _prmReverbAdv;

    // ── Section accent colours ────────────────────────────────────────────────

    private static readonly IBrush OscAccent   = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush FiltAccent  = new SolidColorBrush(Color.Parse("#40B0F0"));
    private static readonly IBrush EnvAccent   = new SolidColorBrush(Color.Parse("#70C870"));
    private static readonly IBrush LfoAccent   = new SolidColorBrush(Color.Parse("#B070D8"));
    private static readonly IBrush VoiceAccent = new SolidColorBrush(Color.Parse("#F07840"));
    private static readonly IBrush FxAccent    = new SolidColorBrush(Color.Parse("#40C8A8"));

    public MainWindow()
    {
        _prmDelayMain  = BuildPrmDelayMain();
        _prmReverbMain = BuildPrmReverbMain();
        _prmDelayAdv   = BuildPrmDelayAdv();
        _prmReverbAdv  = BuildPrmReverbAdv();

        InitializeComponent();
        PopulateDeviceLists();
        BuildSectionPanels();

        ConnectButton.Click  += OnConnectClicked;
        SendAllButton.Click  += OnSendAllClicked;
        SaveButton.Click     += OnSaveClicked;
        LoadButton.Click     += OnLoadClicked;
        SyncButton.Click     += OnSyncClicked;
        SyncDoneButton.Click += OnSyncDoneClicked;
        OpenPrmButton.Click  += OnOpenPrmClicked;
        SavePrmButton.Click  += OnSavePrmClicked;
    }

    // ── Device lists ──────────────────────────────────────────────────────────

    private void PopulateDeviceLists()
    {
        // Output list — managed-midi (works fine on .NET 10)
        foreach (var port in _midi.Outputs)
            DeviceCombo.Items.Add(port.Name);

        // Input list — DryWetMidi (reliable on .NET 10; managed-midi input fails with MMSYSERR_INVALPARAM)
        _inputDevices.AddRange(InputDevice.GetAll());
        foreach (var device in _inputDevices)
            InputCombo.Items.Add(device.Name);

        for (int ch = 1; ch <= 16; ch++)
            ChannelCombo.Items.Add(ch.ToString());
        ChannelCombo.SelectedIndex = 2;   // default: channel 3

        if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
        if (InputCombo.Items.Count  > 0) InputCombo.SelectedIndex  = 0;
    }

    // ── Build section panels ──────────────────────────────────────────────────

    private void BuildSectionPanels()
    {
        // Controls (Mod Wheel, Expression, Damper) are shown inside the Voice panel.
        // CC103 (Overtone) and CC104 (Comb) are rendered inside the chop sub-section below.
        var nonChopOsc = _patch.Oscillator.Where(p => p.CcNumber != 103 && p.CcNumber != 104).ToList();
        PopulateSection(OscillatorPanel, nonChopOsc, OscAccent);
        BuildChopSection();
        PopulateSection(FilterPanel,     _patch.Filter,     FiltAccent);
        PopulateSection(EnvelopePanel,   _patch.Envelope,   EnvAccent);
        PopulateSection(LfoPanel,        _patch.Lfo,        LfoAccent);
        PopulateSection(VoicePanel,      _patch.Controls,   VoiceAccent);
        PopulateSection(VoicePanel,      _patch.Voice,      VoiceAccent);
        BuildEffectsPanel();
    }

    private void PopulateSection(WrapPanel panel,
        IReadOnlyList<S1Parameter> parameters, IBrush accent)
    {
        foreach (var param in parameters)
        {
            panel.Children.Add(param.ParameterType switch
            {
                S1ParameterType.Toggle        => MakeToggle(param),
                S1ParameterType.Dropdown      => MakeDropdown(param),
                S1ParameterType.BipolarSlider => MakeKeyShiftSlider(param),
                _                             => MakeKnob(param, accent),
            });
        }
    }

    // Returns the human-readable display string for a CC-mapped parameter.
    // Special cases override the default PRM-scale conversion from PrmCcMap.
    private static string GetKnobDisplayValue(S1Parameter param)
    {
        int cc  = param.CcNumber;
        int val = param.Value;

        return cc switch
        {
            76  => (val - 64).ToString(),                                                    // Fine Tune: -64 to +63
            90  => $"{1 + (int)Math.Round(val * 739.0 / 127)}ms",                           // Delay Time: 1-740ms
            103 => ((int)Math.Round(val * 255.0 / 127)).ToString(),                         // Overtone: native PRM scale 0-255 (clamped to 200 in slider)
            104 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}",        // Comb: 1.0–32.0 at CC 3–127, rounded to nearest 0.5
            _   => PrmCcMap.ByCC.TryGetValue(cc, out var e) ? e.Info.ToPrm(val).ToString()  // PRM-scale default
                                                             : val.ToString()                // Fallback (Mod Wheel, Expression)
        };
    }

    // Returns the human-readable display string for a PRM-only parameter.
    private static string GetPrmKnobDisplayValue(PrmParameter param)
    {
        int prmVal = param.ToPrm();
        return param.PrmKey == "REVERB_PRE_DELAY" ? $"{prmVal}ms" : prmVal.ToString();
    }

    // Rotary knob + value label + name label for continuous/mode parameters.
    // The knob drives param.Value when dragged, and param.ValueChanged
    // (fired by incoming MIDI) drives the knob back — no echo because
    // UpdateFromMidi doesn't trigger _onSend.
    // minCcValue: lowest CC value that will be sent to hardware (use 3 for Comb).
    private static Control MakeKnob(S1Parameter param, IBrush accent, int minCcValue = 0)
    {
        string initDisplay = GetKnobDisplayValue(param);

        var knob = new RotaryKnob { Value = param.Value, AccentBrush = accent };
        ToolTip.SetTip(knob, $"{param.Name}: {initDisplay}");

        var valueLabel = new TextBlock
        {
            Classes = { "param-value-label" },
            Text    = initDisplay,
        };

        // Knob dragged → update model (which sends CC to hardware)
        knob.ValueChanged += (_, v) =>
        {
            param.Value = Math.Max(minCcValue, v);
            string display = GetKnobDisplayValue(param);
            ToolTip.SetTip(knob, $"{param.Name}: {display}");
            valueLabel.Text = display;
        };

        // Incoming MIDI / PRM load → update knob and label (must marshal to UI thread)
        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                knob.Value = v;   // no-op if already equal
                string display = GetKnobDisplayValue(param);
                ToolTip.SetTip(knob, $"{param.Name}: {display}");
                valueLabel.Text = display;
            });

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            =
            {
                knob,
                valueLabel,
                new TextBlock { Classes = { "param-label" }, Text = param.Name },
            },
        };
    }

    // Drop-down for discrete/mode parameters.
    private static Control MakeDropdown(S1Parameter param)
    {
        var opts  = param.Options!;
        var combo = new ComboBox { Classes = { "param-combo" } };
        foreach (var opt in opts)
            combo.Items.Add(opt);
        combo.SelectedIndex = Math.Clamp(param.Value, 0, opts.Length - 1);

        // User picks → update model (which sends CC)
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                param.Value = combo.SelectedIndex;
        };

        // Incoming MIDI → update dropdown (marshal to UI thread)
        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                var idx = Math.Clamp(v, 0, opts.Length - 1);
                if (combo.SelectedIndex != idx)
                    combo.SelectedIndex = idx;
            });

        var label = new TextBlock
        {
            Classes = { "param-label" },
            Text    = param.Name,
        };

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            = { combo, label },
        };
    }

    // Checkbox for on/off parameters.
    private static Control MakeToggle(S1Parameter param)
    {
        var cb = new CheckBox
        {
            Classes   = { "param-toggle" },
            Content   = param.Name,
            IsChecked = param.Value > 0,
        };

        // Checkbox toggled → update model
        cb.IsCheckedChanged += (_, _) =>
            param.Value = (cb.IsChecked == true) ? 127 : 0;

        // Incoming MIDI → update checkbox (marshal to UI thread)
        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() => cb.IsChecked = v > 0);

        return cb;
    }

    // Horizontal slider for bipolar semitone-offset parameters (CC85/86/87).
    // param.Value stores the CC (0-127); slider position = CC - 64, range ±12.
    private static Control MakeKeyShiftSlider(S1Parameter param)
    {
        const int SemitoneMin = -12;
        const int SemitoneMax =  12;

        static int   ToSemitone(int cc)  => Math.Clamp(cc - 64, SemitoneMin, SemitoneMax);
        static string FormatSt(int s)    => s > 0 ? $"+{s}" : s.ToString();

        int initSt = ToSemitone(param.Value);

        var slider = new Slider
        {
            Minimum             = SemitoneMin,
            Maximum             = SemitoneMax,
            Value               = initSt,
            IsSnapToTickEnabled = true,
            TickFrequency       = 1,
            Width               = 120,
            Orientation         = Orientation.Horizontal,
        };

        var valueLabel = new TextBlock
        {
            Classes             = { "param-label" },
            Text                = FormatSt(initSt),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var nameLabel = new TextBlock
        {
            Classes = { "param-label" },
            Text    = param.Name,
        };

        slider.ValueChanged += (_, e) =>
        {
            int s = (int)Math.Round(e.NewValue);
            param.Value    = s + 64;
            valueLabel.Text = FormatSt(s);
        };

        param.ValueChanged += (_, cc) =>
            Dispatcher.UIThread.Post(() =>
            {
                int s = ToSemitone(cc);
                slider.Value    = s;
                valueLabel.Text = FormatSt(s);
            });

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            = { slider, valueLabel, nameLabel },
        };
    }

    // ── OSC Chop sub-section ──────────────────────────────────────────────────

    private void BuildChopSection()
    {
        // Divider + subtitle
        ChopGridPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#444444")),
            Margin     = new Thickness(0, 10, 0, 6),
        });
        ChopGridPanel.Children.Add(new TextBlock
        {
            Text       = "OSC CHOP",
            FontSize   = 10,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Foreground = OscAccent,
            Margin     = new Thickness(0, 0, 0, 6),
        });

        // Hint label — replaces the removed Chop On/Off dropdown
        ChopGridPanel.Children.Add(new TextBlock
        {
            Text         = "Set Overtone above 0 to activate chop effect",
            FontSize     = 9,
            Foreground   = new SolidColorBrush(Color.Parse("#888888")),
            FontStyle    = Avalonia.Media.FontStyle.Italic,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        // Top controls row: Comb knob + Overtone slider
        var topRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        topRow.Children.Add(MakeKnob(_patch.GetByCC(104)!, OscAccent, minCcValue: 3));  // Comb (hw min CC=3)
        topRow.Children.Add(MakeOvertoneSlider(_patch.GetByCC(103)!));                  // Overtone 0-200
        ChopGridPanel.Children.Add(topRow);

        // 4-row step grid
        var grid = new StackPanel { Spacing = 4 };

        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            int waveform = w;

            // Capture arrays for the PatternChanged closure
            var stepBtns = new Button[ChopPattern.Steps];

            // Row 1: label + 16 step buttons
            var stepRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 0,
            };

            stepRow.Children.Add(new TextBlock
            {
                Text              = ChopPattern.WaveformNames[w],
                Width             = 42,
                FontSize          = 10,
                Foreground        = new SolidColorBrush(Color.Parse("#BBBBBB")),
                VerticalAlignment = VerticalAlignment.Center,
            });

            for (int s = 0; s < ChopPattern.Steps; s++)
            {
                int step = s;
                bool isOn = _chopPattern.GetStep(w, s);

                var btn = new Button
                {
                    Width           = 16,
                    Height          = 16,
                    Padding         = new Thickness(0),
                    MinWidth        = 0,
                    MinHeight       = 0,
                    Margin          = new Thickness(1, 0),
                    Background      = isOn ? s_chopOnBrush : s_chopOffBrush,
                    BorderBrush     = s_chopBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(2),
                };

                btn.Click += (_, _) =>
                {
                    bool newOn = !_chopPattern.GetStep(waveform, step);
                    _chopPattern.SetStep(waveform, step, newOn);
                    btn.Background = newOn ? s_chopOnBrush : s_chopOffBrush;
                };

                stepBtns[s] = btn;
                stepRow.Children.Add(btn);
            }

            // Row 2: indented utility buttons
            var utilRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 4,
                Margin      = new Thickness(42, 2, 0, 0),
            };

            var allOnBtn = new Button
            {
                Content  = "All On",
                FontSize = 9,
                Padding  = new Thickness(6, 2),
                MinWidth = 0,
            };
            var allOffBtn = new Button
            {
                Content  = "All Off",
                FontSize = 9,
                Padding  = new Thickness(6, 2),
                MinWidth = 0,
            };

            allOnBtn.Click  += (_, _) => _chopPattern.SetAll(waveform, true);
            allOffBtn.Click += (_, _) => _chopPattern.SetAll(waveform, false);

            utilRow.Children.Add(allOnBtn);
            utilRow.Children.Add(allOffBtn);

            // Sync all buttons when the full pattern is replaced (SetAll / LoadFromPrm)
            _chopPattern.PatternChanged += changedWaveform =>
            {
                if (changedWaveform != waveform) return;
                Dispatcher.UIThread.Post(() =>
                {
                    for (int s = 0; s < ChopPattern.Steps; s++)
                        stepBtns[s].Background = _chopPattern.GetStep(waveform, s)
                            ? s_chopOnBrush : s_chopOffBrush;
                });
            };

            var waveformBlock = new StackPanel { Spacing = 0 };
            waveformBlock.Children.Add(stepRow);
            waveformBlock.Children.Add(utilRow);
            grid.Children.Add(waveformBlock);
        }

        ChopGridPanel.Children.Add(grid);

        // Footer note
        ChopGridPanel.Children.Add(new TextBlock
        {
            Text         = "Chop patterns require saving to .PRM to take effect on hardware.",
            FontSize     = 9,
            Foreground   = new SolidColorBrush(Color.Parse("#777777")),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 6, 0, 0),
        });
    }

    // Horizontal slider for CC103 Overtone — displays native PRM scale 0-255,
    // clamped to 200 (device ignores PRM values above 200; model stores CC 0-127).
    private static Control MakeOvertoneSlider(S1Parameter param)
    {
        static int ToDisplay(int cc)      => (int)Math.Round(cc * 255.0 / 127);
        static int ToCcValue(int display) => (int)Math.Round(display * 127.0 / 255);

        int initDisplay = Math.Min(ToDisplay(param.Value), 200);

        var slider = new Slider
        {
            Minimum             = 0,
            Maximum             = 200,
            Value               = initDisplay,
            IsSnapToTickEnabled = true,
            TickFrequency       = 1,
            Width               = 130,
            Orientation         = Orientation.Horizontal,
        };

        var valueLabel = new TextBlock
        {
            Classes             = { "param-label" },
            Text                = initDisplay.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        slider.ValueChanged += (_, e) =>
        {
            int d = (int)Math.Round(e.NewValue);
            param.Value     = ToCcValue(d);
            valueLabel.Text = d.ToString();
        };

        param.ValueChanged += (_, cc) =>
            Dispatcher.UIThread.Post(() =>
            {
                int d = ToDisplay(cc);
                slider.Value    = d;
                valueLabel.Text = d.ToString();
            });

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            =
            {
                slider,
                valueLabel,
                new TextBlock { Classes = { "param-label" }, Text = param.Name },
            },
        };
    }

    // ── Effects panel (CC + PRM-only controls interleaved) ───────────────────

    private void BuildEffectsPanel()
    {
        // Layer 1 — main controls (always visible)
        EffectsPanel.Children.Add(MakeKnob(_patch.GetByCC(92)!, FxAccent));   // Delay Level
        EffectsPanel.Children.Add(MakeKnob(_patch.GetByCC(90)!, FxAccent));   // Delay Time
        foreach (var p in _prmDelayMain)
            EffectsPanel.Children.Add(MakePrmControl(p, FxAccent));

        EffectsPanel.Children.Add(MakeKnob(_patch.GetByCC(91)!, FxAccent));   // Reverb Level
        EffectsPanel.Children.Add(MakeKnob(_patch.GetByCC(89)!, FxAccent));   // Reverb Time
        foreach (var p in _prmReverbMain)
            EffectsPanel.Children.Add(MakePrmControl(p, FxAccent));

        EffectsPanel.Children.Add(MakeDropdown(_patch.GetByCC(93)!));          // Chorus Type

        // Layer 2 — advanced controls (collapsed by default)
        var advWrap = new WrapPanel();
        foreach (var p in _prmDelayAdv)
            advWrap.Children.Add(MakePrmControl(p, FxAccent));
        foreach (var p in _prmReverbAdv)
            advWrap.Children.Add(MakePrmControl(p, FxAccent));

        var expander = new Expander
        {
            Header     = "Advanced",
            IsExpanded = false,
            Margin     = new Thickness(0, 4, 0, 0),
            Content    = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text         = "These settings require saving to .PRM and reloading on the hardware to take effect.",
                        Foreground   = new SolidColorBrush(Color.Parse("#888888")),
                        FontSize     = 9,
                        TextWrapping = TextWrapping.Wrap,
                        Margin       = new Thickness(2, 4, 2, 6),
                    },
                    advWrap,
                }
            },
        };

        EffectsStack.Children.Add(expander);
    }

    // Knob for a PRM-only parameter — value label shows the native PRM scale.
    private static Control MakePrmKnob(PrmParameter param, IBrush accent)
    {
        string initDisplay = GetPrmKnobDisplayValue(param);

        var knob = new RotaryKnob { Value = param.Value, AccentBrush = accent };
        ToolTip.SetTip(knob, $"{param.Name}: {initDisplay}");

        var valueLabel = new TextBlock
        {
            Classes = { "param-value-label" },
            Text    = initDisplay,
        };

        knob.ValueChanged += (_, v) =>
        {
            param.Value = v;
            string display = GetPrmKnobDisplayValue(param);
            ToolTip.SetTip(knob, $"{param.Name}: {display}");
            valueLabel.Text = display;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                knob.Value = v;
                string display = GetPrmKnobDisplayValue(param);
                ToolTip.SetTip(knob, $"{param.Name}: {display}");
                valueLabel.Text = display;
            });

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            =
            {
                knob,
                valueLabel,
                new TextBlock { Classes = { "param-label" }, Text = param.Name },
            },
        };
    }

    // Dropdown for a PRM-only parameter.
    private static Control MakePrmDropdown(PrmParameter param)
    {
        var opts  = param.Options!;
        var combo = new ComboBox { Classes = { "param-combo" } };
        foreach (var opt in opts)
            combo.Items.Add(opt);
        combo.SelectedIndex = param.Value;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                param.Value = combo.SelectedIndex;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (combo.SelectedIndex != v)
                    combo.SelectedIndex = v;
            });

        var label = new TextBlock { Classes = { "param-label" }, Text = param.Name };

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            = { combo, label },
        };
    }

    private static Control MakePrmControl(PrmParameter param, IBrush accent) =>
        param.Options is not null ? MakePrmDropdown(param) : MakePrmKnob(param, accent);

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private async void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedIndex < 0)
        {
            SetStatus("Select a MIDI output device first.", "#FF6B6B");
            return;
        }

        var outPorts = new List<IMidiPortDetails>(_midi.Outputs);
        var outPort  = outPorts[DeviceCombo.SelectedIndex];

        // ── Output connection (required) ──────────────────────────────────
        try
        {
            await _patch.ConnectAsync(_midi, outPort.Id, channel: MidiChannel);
        }
        catch (Exception ex)
        {
            SetStatus($"Output error: {ex.Message}", "#FF6B6B");
            return;
        }

        ConnectButton.Content   = "Reconnect";
        SendAllButton.IsEnabled = true;
        SyncButton.IsEnabled    = true;

        // ── Input connection via DryWetMidi (optional) ───────────────────
        if (InputCombo.SelectedIndex < 0 || InputCombo.SelectedIndex >= _inputDevices.Count)
        {
            SetStatus($"→ {outPort.Name}  (no input selected)", "#70C870");
            return;
        }

        try
        {
            // Stop any previous input session before opening a new one.
            _activeInput?.StopEventsListening();
            _activeInput = _inputDevices[InputCombo.SelectedIndex];
            _activeInput.EventReceived += OnMidiEventReceived;
            _activeInput.StartEventsListening();
            SetStatus($"↔ {outPort.Name}  |  listening on {_activeInput.Name}", "#70C870");
        }
        catch (Exception ex)
        {
            SetStatus($"→ {outPort.Name}  (input unavailable: {ex.Message})", "#F0A040");
        }
    }

    // Receives all MIDI events from the selected input device.
    // Routes to the sync session buffer (if active) or directly to the patch model.
    private void OnMidiEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        if (e.Event is not ControlChangeEvent cc) return;
        if ((int)cc.Channel != MidiChannel - 1) return;

        int ccNumber = (int)cc.ControlNumber;
        int value    = (int)cc.ControlValue;

        if (_syncSession is not null)
        {
            // Must call DispatcherTimer methods on the UI thread.
            Dispatcher.UIThread.Post(() => _syncSession?.RecordCC(ccNumber, value));
        }
        else
        {
            _patch.HandleIncomingCC(ccNumber, value);
        }
    }

    private async void OnSendAllClicked(object? sender, RoutedEventArgs e)
    {
        SendAllButton.IsEnabled = false;
        SetStatus("Sending…", "#AAAAAA");
        await _patch.SendAllAsync();
        SendAllButton.IsEnabled = true;
        SetStatus("All parameters sent.", "#70C870");
    }

    // ── Sync from Hardware ────────────────────────────────────────────────────

    private void OnSyncClicked(object? sender, RoutedEventArgs e)
    {
        // Start a fresh session.
        _syncSession?.Dispose();
        _syncSession = new HardwareSyncSession();

        _syncSession.ParameterSettled += OnParameterSettled;

        // Show the overlay and disable toolbar buttons that shouldn't be used mid-sync.
        SyncOverlay.IsVisible   = true;
        SyncButton.IsEnabled    = false;
        SendAllButton.IsEnabled = false;
        // Exclude Mod Wheel (CC1) and Expression (CC11) — physical controllers
        // whose position cannot be read by wiggling synth knobs.
        int syncableCount = _patch.AllParameters.Count(p => p.CcNumber != 1 && p.CcNumber != 11);
        SyncProgressText.Text   = $"Captured: 0 / {syncableCount}";
    }

    // Called (on UI thread via DispatcherTimer) each time a CC settles.
    private void OnParameterSettled(int ccNumber, int median)
    {
        // Write the settled median immediately so the UI knob updates live.
        _patch.HandleIncomingCC(ccNumber, median);

        int syncableCount = _patch.AllParameters.Count(p => p.CcNumber != 1 && p.CcNumber != 11);
        SyncProgressText.Text =
            $"Captured: {_syncSession?.CapturedCount ?? 0} / {syncableCount}";
    }

    private async void OnSyncDoneClicked(object? sender, RoutedEventArgs e)
    {
        if (_syncSession is null) return;

        // Commit any CCs whose debounce timer hasn't fired yet
        // (e.g. the user clicked Done while still wiggling).
        var final = _syncSession.GetFinalValues();
        foreach (var (cc, value) in final)
            _patch.HandleIncomingCC(cc, value);

        _syncSession.Dispose();
        _syncSession = null;

        // Hide overlay and restore toolbar.
        SyncOverlay.IsVisible   = false;
        SendAllButton.IsEnabled = true;
        SyncButton.IsEnabled    = true;

        // Send all current values to hardware so it matches the editor.
        SetStatus("Syncing to hardware…", "#AAAAAA");
        await _patch.SendAllAsync();
        SetStatus("Sync complete.", "#70C870");
    }

    // ── Preset save / load ────────────────────────────────────────────────────

    private static readonly FilePickerFileType S1PatchFileType =
        new("Roland S-1 Patch") { Patterns = new[] { "*.s1patch" } };

    private static readonly FilePickerFileType PrmFileType =
        new("Roland S-1 PRM Patch") { Patterns = new[] { "*.PRM", "*.prm" } };

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Preset",
            SuggestedFileName = PresetNameBox.Text?.Trim() is { Length: > 0 } n ? n : "preset",
            DefaultExtension  = "s1patch",
            FileTypeChoices   = new[] { S1PatchFileType },
        });

        if (file is null) return;

        var preset = _patch.ToPreset(PresetNameBox.Text ?? "Untitled");
        preset.PrmOnly = AllPrmOnlyParams()
            .Select(p => new PrmOnlyEntry { PrmKey = p.PrmKey, Value = p.Value })
            .ToList();
        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, preset, JsonOptions);

        SetStatus($"Saved: {file.Name}", "#70C870");
    }

    private async void OnLoadClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Load Preset",
            AllowMultiple  = false,
            FileTypeFilter = new[] { S1PatchFileType },
        });

        if (files.Count == 0) return;

        S1PresetFile? preset;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            preset = await JsonSerializer.DeserializeAsync<S1PresetFile>(stream);
        }
        catch (Exception ex)
        {
            SetStatus($"Load error: {ex.Message}", "#FF6B6B");
            return;
        }

        if (preset is null) return;

        // Update the name field and all CC parameter values in the model + UI.
        PresetNameBox.Text = preset.Name;
        _patch.LoadPreset(preset);

        // Restore PRM-only parameters if present (older files may have an empty list).
        if (preset.PrmOnly.Count > 0)
        {
            var prmLookup = AllPrmOnlyParams().ToDictionary(p => p.PrmKey);
            foreach (var entry in preset.PrmOnly)
            {
                if (prmLookup.TryGetValue(entry.PrmKey, out var prm))
                    prm.Value = entry.Value;
            }
        }

        // Sync the hardware to the newly loaded values.
        await _patch.SendAllAsync();

        SetStatus($"Loaded: {preset.Name}", "#70C870");
    }

    // ── PRM file open / save ──────────────────────────────────────────────────

    private async void OnOpenPrmClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open PRM Patch File",
            AllowMultiple  = false,
            FileTypeFilter = new[] { PrmFileType },
        });

        if (files.Count == 0) return;

        PrmFileData parsed;
        try
        {
            parsed = PrmFileParser.Parse(files[0].TryGetLocalPath()!);
        }
        catch (Exception ex)
        {
            SetStatus($"PRM load error: {ex.Message}", "#FF6B6B");
            return;
        }

        _lastPrmData = parsed;

        // Map every known PRM key → CC, apply per-parameter scaling, push into model.
        foreach (var (key, rawValue) in parsed.Parameters)
        {
            if (!PrmCcMap.Map.TryGetValue(key, out var info)) continue;
            if (!int.TryParse(rawValue, out int prmValue)) continue;
            _patch.HandleIncomingCC(info.Cc, info.ToCc(prmValue));
        }

        // Mod Wheel and Expression are physical controllers — reset to neutral defaults
        // regardless of whatever the PRM file may have stored.
        _patch.HandleIncomingCC(1,  0);    // Mod Wheel = 0
        _patch.HandleIncomingCC(11, 127);  // Expression = 127 (fully open)

        // Load PRM-only effect parameters (Sync, Type, Feedback, EQ, etc.)
        LoadPrmOnly(parsed, _prmDelayMain);
        LoadPrmOnly(parsed, _prmReverbMain);
        LoadPrmOnly(parsed, _prmDelayAdv);
        LoadPrmOnly(parsed, _prmReverbAdv);
        // Comb (CC104): PRM stores 1-32 integers; CC = clamp(3 + round((prmValue-1)*124/31), 3, 127)
        // This maps PRM 1→CC 3 (display 1.7) and PRM 32→CC 127 (display 32.0).
        if (parsed.Parameters.TryGetValue("OSC_CHOP_COMB", out var combRaw) &&
            int.TryParse(combRaw, out int combPrm))
        {
            int combCc = Math.Clamp(3 + (int)Math.Round((combPrm - 1) * 124.0 / 31.0), 3, 127);
            _patch.HandleIncomingCC(104, combCc);
        }

        // Load chop step patterns (bit-packed integers, one per waveform).
        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            if (parsed.Parameters.TryGetValue(ChopPattern.PrmKeys[w], out var rawStr) &&
                int.TryParse(rawStr, out int rawVal))
                _chopPattern.LoadFromPrm(w, rawVal);
        }

        // Show filename (no path, no extension) as preset name.
        var fileName = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
        PresetNameBox.Text = fileName;

        // Sync hardware to the newly loaded values.
        SetStatus("Sending PRM values…", "#AAAAAA");
        await _patch.SendAllAsync();
        SetStatus($"Loaded PRM: {fileName}", "#70C870");
    }

    private async void OnSavePrmClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save PRM Patch File",
            SuggestedFileName = PresetNameBox.Text?.Trim() is { Length: > 0 } n ? n : "PATCH",
            DefaultExtension  = "PRM",
            FileTypeChoices   = new[] { PrmFileType },
        });

        if (file is null) return;

        try
        {
            var localPath = file.TryGetLocalPath()!;

            var allPrmOnly = _prmDelayMain
                .Concat(_prmReverbMain)
                .Concat(_prmDelayAdv)
                .Concat(_prmReverbAdv);

            // Comb (CC104) inverse: PRM = clamp(1 + round((cc-3)*31/124), 1, 32)
            // Maps CC 3→PRM 1 and CC 127→PRM 32.
            var combParam  = _patch.GetByCC(104);
            int combPrmVal = combParam is not null
                ? Math.Clamp(1 + (int)Math.Round((combParam.Value - 3) * 31.0 / 124.0), 1, 32)
                : 1;

            var chopRaw = Enumerable.Range(0, ChopPattern.Waveforms)
                .Select(w => (ChopPattern.PrmKeys[w], _chopPattern.ToPrm(w)))
                .Append(("OSC_CHOP_COMB", combPrmVal));

            PrmFileParser.Serialize(localPath, _patch, _lastPrmData, allPrmOnly, chopRaw);
            SetStatus($"Saved PRM: {file.Name}", "#70C870");
        }
        catch (Exception ex)
        {
            SetStatus($"PRM save error: {ex.Message}", "#FF6B6B");
        }
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

    // All PRM-only effect parameters in a single flat enumeration.
    private IEnumerable<PrmParameter> AllPrmOnlyParams() =>
        _prmDelayMain.Concat(_prmReverbMain).Concat(_prmDelayAdv).Concat(_prmReverbAdv);

    private void SetStatus(string message, string hexColour)
    {
        StatusText.Text       = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(hexColour));
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _activeInput?.StopEventsListening();
        foreach (var d in _inputDevices) d.Dispose();
        _patch.Dispose();
        base.OnClosed(e);
    }
}
