using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
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

    // Chop step-pattern model (4 waveforms × 16 steps, PRM-only).
    private readonly ChopPattern _chopPattern = new();

    // Draw waveform points (8 signed-16-bit amplitudes, PRM-only).
    private readonly DrawWave _drawWave = new();

    // Step sequencer data (up to 64 steps, PRM-only).
    private readonly SequencerData _sequence = new();

    // Patch/pattern bank buttons (4 groups × 16 patterns = 64 program changes).
    private readonly List<Button> _patchButtons = new();

    private bool   _autoConnect;
    private bool   _filterModEnabled;
    private bool   _patternSync;
    private bool   _suppressPatternSyncToggle;
    private string _prmFolder = "";
    private static readonly string SettingsPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "s1editor.settings.json");

    // ── Filter modulation animation ───────────────────────────────────────────

    private enum EnvPhase { Off, Attack, Decay, Sustain, Release }

    private Action?  _filterCurveUpdate;
    private Action?  _envelopeDotUpdate;
    private double   _filterModOffset;
    private EnvPhase _envPhase = EnvPhase.Off;
    private double   _envLevel;
    private double   _envLevelAtRelease;
    private int      _noteCount;
    private double   _lfoPhase;
    private double   _lfoRandom;
    private DateTime _lastModTick;
    private readonly DispatcherTimer _modTimer = new();

    // Brushes reused across all 64 step buttons.
    private static readonly IBrush s_chopOnBrush  = new SolidColorBrush(Color.Parse("#CC2222"));
    private static readonly IBrush s_chopOffBrush = new SolidColorBrush(Color.Parse("#383838"));
    private static readonly IBrush s_chopBorder   = new SolidColorBrush(Color.Parse("#505050"));

    // ── PRM-only effect parameters ────────────────────────────────────────────

    private static readonly string[] s_lowCutOpts = {
        "Flat","20","25","31.5","40","50","63","80","100","125",
        "160","200","250","315","400","500","630","800"
    };
    private static readonly string[] s_highCutOpts = {
        "630","800","1k","1.25k","1.6k","2k","2.5k","3.15k",
        "4k","5k","6.3k","8k","10k","12.5k","Flat"
    };

    private static List<PrmParameter> BuildPrmDelayMain() => new()
    {
        new("Delay Sync", "DELAY_SW", options: new[] { "Off", "Sync to Tempo" }),
    };

    private static List<PrmParameter> BuildPrmReverbMain() => new()
    {
        new("Reverb Type", "REVERB_TYPE", options: new[] {
            "Ambience","Room","Hall 1","Hall 2","Plate","Spring","Modulate" }),
    };

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

    private readonly PrmParameter _delayTempo = new("Delay Tempo", "DELAY_TEMPO", options: new[] {
        "128", "64t", "128d", "1_64", "32t", "64d", "1_32", "16t",
        "32d", "1_16", "8t", "16d", "1_8", "4t", "8d", "1_4" });

    // OSC chop PRM-only
    private readonly PrmParameter _prmChopType     = new("Chop Type",  "OSC_CHOP_TYPE",      prmMax: 7);
    private readonly PrmParameter _prmChopCombType = new("Comb Type",  "OSC_CHOP_COMB_TYPE", prmMax: 7);

    // Pattern globals
    private readonly PrmParameter _prmLeng      = new("Length",     "LENG",       prmMax: 64);
    private readonly PrmParameter _prmShuffle   = new("Shuffle",    "SHUFFLE",    prmMax: 50);
    private readonly PrmParameter _prmLevel     = new("Level",      "LEVEL",      prmMax: 127);
    private readonly PrmParameter _prmScale     = new("Scale",      "SCALE",      prmMax: 7);
    private readonly PrmParameter _prmTempoSync = new("Tempo Sync", "TEMPO_SYNC", options: new[] { "Off", "On" });

    // Arpeggiator
    private readonly PrmParameter _prmArpType = new("Type", "ARP_TYPE", options: new[] {
        "Off", "Up", "Down", "Up/Down", "Random", "Order" });
    private readonly PrmParameter _prmArpRate = new("Rate", "ARP_RATE", prmMax: 7);

    // Riser
    private readonly PrmParameter _prmRiserSw    = new("Riser",     "RISER_SW",    options: new[] { "Off", "On" });
    private readonly PrmParameter _prmRiserMode  = new("Mode",      "RISER_MODE",  options: new[] { "Normal", "Rise", "Fall", "Rise+Fall" });
    private readonly PrmParameter _prmRiserCtrl  = new("Target",    "RISER_CTRL",  prmMax: 127);
    private readonly PrmParameter _prmRiserBeat  = new("Beat",      "RISER_BEAT",  prmMax: 15);
    private readonly PrmParameter _prmRiserShape = new("Shape",     "RISER_SHAPE", prmMax: 7);
    private readonly PrmParameter _prmRiserReso  = new("Resonance", "RISER_RESO",  prmMax: 100);
    private readonly PrmParameter _prmRiserLevel = new("Level",     "RISER_LEVEL", prmMax: 100);

    // D-Motion
    private readonly PrmParameter _prmDmAssignX   = new("X Assign",   "DM_ASSIGN_X",   prmMax: 15);
    private readonly PrmParameter _prmDmAssignY   = new("Y Assign",   "DM_ASSIGN_Y",   prmMax: 15);
    private readonly PrmParameter _prmDmAssignTap = new("Tap Assign", "DM_ASSIGN_TAP", prmMax: 15);
    private readonly PrmParameter _prmDmAssignFf  = new("FF Assign",  "DM_ASSIGN_FF",  prmMax: 15);
    private readonly PrmParameter _prmDmSensX     = new("X Sens",     "DM_SENS_X",     prmMax: 10);
    private readonly PrmParameter _prmDmSensY     = new("Y Sens",     "DM_SENS_Y",     prmMax: 10);

    // Labels for values that need special formatting (tempo as BPM, signed transpose, motion CC names)
    private readonly TextBlock   _tempoLabel     = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };
    private readonly TextBlock   _transposeLabel = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };
    private readonly TextBlock[] _motionCcLabels = new TextBlock[8];

    // 31 synced LFO rate values, indexed by CC 0–30 (slowest → fastest).
    private static readonly string[] s_lfoSyncValues =
    {
        "8_1",  "6_1",  "8_1t", "4_1",  "3_1",  "4_1t",
        "2_1",  "1_1d", "2_1t", "1_1",  "2d",   "1_1t",
        "1_2",  "4d",   "1_2t", "1_4",  "8d",   "4t",
        "1_8",  "16d",  "8t",   "1_16", "32d",  "16t",
        "1_32", "64d",  "32t",  "1_64", "128d", "64t",  "128",
    };

    // ── Section accent colours ────────────────────────────────────────────────

    private static readonly IBrush OscAccent   = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush SeqAccent   = new SolidColorBrush(Color.Parse("#E0C040"));
    private static readonly IBrush FiltAccent  = new SolidColorBrush(Color.Parse("#40B0F0"));
    private static readonly IBrush EnvAccent   = new SolidColorBrush(Color.Parse("#70C870"));
    private static readonly IBrush LfoAccent   = new SolidColorBrush(Color.Parse("#B070D8"));
    private static readonly IBrush VoiceAccent = new SolidColorBrush(Color.Parse("#F07840"));
    private static readonly IBrush FxAccent    = new SolidColorBrush(Color.Parse("#40C8A8"));
    private static readonly IBrush DmAccent    = new SolidColorBrush(Color.Parse("#60A8E0"));

    public MainWindow()
    {
        _prmDelayMain  = BuildPrmDelayMain();
        _prmReverbMain = BuildPrmReverbMain();
        _prmDelayAdv   = BuildPrmDelayAdv();
        _prmReverbAdv  = BuildPrmReverbAdv();

        for (int i = 0; i < 8; i++)
            _motionCcLabels[i] = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };

        InitializeComponent();
        PopulateDeviceLists();
        BuildRealtimeEditorPanels();
        BuildPrmViewerContent();
        ApplyInitPatch();

        ConnectButton.Click          += OnConnectClicked;
        SendAllButton.Click          += OnSendAllClicked;
        SaveButton.Click             += OnSaveClicked;
        LoadButton.Click             += OnLoadClicked;
        OpenPrmButton.Click          += OnOpenPrmClicked;
        BrowsePrmFolderButton.Click  += OnBrowsePrmFolderClicked;

        LoadSettings();
        PrmFolderBox.Text = _prmFolder;
        AutoConnectToggle.IsChecked = _autoConnect;
        AutoConnectToggle.IsCheckedChanged += (_, _) =>
        {
            _autoConnect = AutoConnectToggle.IsChecked == true;
            SaveSettings();
        };
        if (_autoConnect) TryAutoConnect();

        FilterModToggle.IsChecked = _filterModEnabled;
        FilterModToggle.IsCheckedChanged += (_, _) =>
        {
            _filterModEnabled = FilterModToggle.IsChecked == true;
            if (!_filterModEnabled) { _filterModOffset = 0; _filterCurveUpdate?.Invoke(); _envelopeDotUpdate?.Invoke(); }
            SaveSettings();
        };

        PatternSyncToggle.IsEnabled = !string.IsNullOrEmpty(_prmFolder);
        PatternSyncToggle.IsChecked = _patternSync;
        PatternSyncToggle.IsCheckedChanged += async (_, _) =>
        {
            if (_suppressPatternSyncToggle) return;
            bool enabling = PatternSyncToggle.IsChecked == true;
            if (enabling && !_patternSync)
            {
                bool confirmed = await ShowPatternSyncWarningAsync();
                if (!confirmed)
                {
                    _suppressPatternSyncToggle = true;
                    PatternSyncToggle.IsChecked = false;
                    _suppressPatternSyncToggle = false;
                    return;
                }
            }
            _patternSync = PatternSyncToggle.IsChecked == true;
            SaveSettings();
        };

        _lastModTick = DateTime.UtcNow;
        _modTimer.Interval = TimeSpan.FromMilliseconds(16);
        _modTimer.Tick += OnModTimerTick;
        _modTimer.Start();
    }

    // ── Device lists ──────────────────────────────────────────────────────────

    private void PopulateDeviceLists()
    {
        foreach (var port in _midi.Outputs)
            DeviceCombo.Items.Add(port.Name);

        _inputDevices.AddRange(InputDevice.GetAll());
        foreach (var device in _inputDevices)
            InputCombo.Items.Add(device.Name);

        for (int ch = 1; ch <= 16; ch++)
            ChannelCombo.Items.Add(ch.ToString());
        ChannelCombo.SelectedIndex = 2;

        if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
        if (InputCombo.Items.Count  > 0) InputCombo.SelectedIndex  = 0;
    }

    // ── Tab 1: Realtime editor ────────────────────────────────────────────────

    private void BuildRealtimeEditorPanels()
    {
        BuildOscPanel();
        BuildFilterPanel();
        BuildEnvelopePanel();
        BuildLfoPanel();
        BuildEffectsPanel();
        BuildVoicePanel();
        BuildPatchPanel();
    }

    private void BuildOscPanel()
    {
        // Row 1: level knobs
        var knobRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 19, 20, 21, 23 })
            knobRow1.Children.Add(MakeKnob(_patch.GetByCC(cc)!, OscAccent));
        OscillatorPanel.Children.Add(knobRow1);

        // Row 2: modulation / tuning knobs
        var knobRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 15, 13, 76, 18 })
            knobRow2.Children.Add(MakeKnob(_patch.GetByCC(cc)!, OscAccent));
        OscillatorPanel.Children.Add(knobRow2);

        // Button strips stacked vertically — all full-width, uniform button cells per strip
        OscillatorPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(14)!, OscAccent));  // Range
        OscillatorPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(78)!, OscAccent));  // Noise Mode
        OscillatorPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(16)!, OscAccent));  // PWM Source
        OscillatorPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(22)!, OscAccent));  // Sub Octave

        OscillatorPanel.Children.Add(MakeSubSectionHeader("DRAW · CHOP", OscAccent));

        var dcKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        dcKnobs.Children.Add(MakeKnob(_patch.GetByCC(102)!, OscAccent, minCcValue: 3));
        dcKnobs.Children.Add(MakeKnob(_patch.GetByCC(104)!, OscAccent, minCcValue: 3));
        dcKnobs.Children.Add(MakeKnob(_patch.GetByCC(103)!, OscAccent));
        OscillatorPanel.Children.Add(dcKnobs);

        OscillatorPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(107)!, OscAccent));
    }

    private void BuildFilterPanel()
    {
        FilterPanel.Children.Add(MakeFilterCurve(_patch.GetByCC(74)!, _patch.GetByCC(71)!));

        var filtRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 74, 71, 24 })
            filtRow1.Children.Add(MakeKnob(_patch.GetByCC(cc)!, FiltAccent));
        FilterPanel.Children.Add(filtRow1);

        var filtRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 25, 26, 27 })
            filtRow2.Children.Add(MakeKnob(_patch.GetByCC(cc)!, FiltAccent));
        FilterPanel.Children.Add(filtRow2);
    }

    private void BuildEnvelopePanel()
    {
        EnvelopePanel.Children.Add(MakeAdsrVisualizer(
            _patch.GetByCC(73)!, _patch.GetByCC(75)!, _patch.GetByCC(30)!, _patch.GetByCC(72)!));

        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 73, 75, 30, 72 })
            knobs.Children.Add(MakeKnob(_patch.GetByCC(cc)!, EnvAccent));
        EnvelopePanel.Children.Add(knobs);

        var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var ampBtn = MakeLedButtonGroup(_patch.GetByCC(28)!, EnvAccent);
        var trgBtn = MakeLedButtonGroup(_patch.GetByCC(29)!, EnvAccent);
        Grid.SetColumn(ampBtn, 0); Grid.SetColumn(trgBtn, 1);
        btnGrid.Children.Add(ampBtn); btnGrid.Children.Add(trgBtn);
        EnvelopePanel.Children.Add(btnGrid);
    }

    private void BuildLfoPanel()
    {
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        knobs.Children.Add(MakeLfoRateKnob());
        knobs.Children.Add(MakeKnob(_patch.GetByCC(17)!, LfoAccent));
        LfoPanel.Children.Add(knobs);

        LfoPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(12)!, LfoAccent));

        var modeRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 2,
            Margin              = new Thickness(3, 1, 3, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        modeRow.Children.Add(MakeLedButtonGroup(_patch.GetByCC(79)!,  LfoAccent));
        modeRow.Children.Add(MakeLedButtonGroup(_patch.GetByCC(106)!, LfoAccent));
        modeRow.Children.Add(MakeLedButtonGroup(_patch.GetByCC(105)!, LfoAccent));
        LfoPanel.Children.Add(modeRow);
    }

    private void BuildVoicePanel()
    {
        // 5 knobs × 56px = 280px + margins ≈ 300px — fits the column without overflow
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 1, 11, 5, 10, 77 })
            knobs.Children.Add(MakeKnob(_patch.GetByCC(cc)!, VoiceAccent, containerWidth: 56));
        VoicePanel.Children.Add(knobs);

        // Portamento and Polyphony stacked vertically — side-by-side caused overflow with 6-option strip
        VoicePanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(31)!, VoiceAccent));
        VoicePanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(80)!, VoiceAccent));

        VoicePanel.Children.Add(MakeDroneButton(_patch.GetByCC(64)!, VoiceAccent));

        var chordSection = new StackPanel();
        chordSection.Children.Add(MakeSubSectionHeader("CHORD", VoiceAccent));
        chordSection.Children.Add(MakeChordVoiceRow(2, _patch.GetByCC(81)!, _patch.GetByCC(85)!, VoiceAccent));
        chordSection.Children.Add(MakeChordVoiceRow(3, _patch.GetByCC(82)!, _patch.GetByCC(86)!, VoiceAccent));
        chordSection.Children.Add(MakeChordVoiceRow(4, _patch.GetByCC(83)!, _patch.GetByCC(87)!, VoiceAccent));
        VoicePanel.Children.Add(chordSection);

        var polyParam = _patch.GetByCC(80)!;
        void UpdateChordEnabled(int v)
        {
            bool isChord = v == 3;
            chordSection.IsEnabled = isChord;
            chordSection.Opacity   = isChord ? 1.0 : 0.3;
        }
        UpdateChordEnabled(polyParam.Value);
        polyParam.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() => UpdateChordEnabled(v));

        var portModeParam = _patch.GetByCC(31)!;
        var portOnParam   = _patch.GetByCC(65)!;
        void SyncPortamentoOn(int modeVal) =>
            portOnParam.Value = modeVal > 0 ? 127 : 0;
        SyncPortamentoOn(portModeParam.Value);
        portModeParam.ValueChanged += (_, v) => SyncPortamentoOn(v);
    }

    // ── LED segmented button group (replaces ComboBox / CheckBox in Tab 1) ───────

    private static Control MakeLedButtonGroup(S1Parameter param, IBrush accent)
    {
        string[] opts = param.Options
            ?? (param.ParameterType == S1ParameterType.Toggle ? new[] { "Off", "On" } : new[] { "0", "1" });

        bool waveIcons = param.CcNumber == 12;

        var borders      = new Border[opts.Length];
        var setColor     = new Action<IBrush>[opts.Length];

        int GetIndex(int v) => param.ParameterType == S1ParameterType.Toggle
            ? (v > 0 ? 1 : 0)
            : Math.Clamp(v, 0, opts.Length - 1);

        void Refresh(int val)
        {
            int active = GetIndex(val);
            for (int i = 0; i < borders.Length; i++)
            {
                bool on = i == active;
                borders[i].Background  = on
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))
                    : new SolidColorBrush(Color.Parse("#191919"));
                borders[i].BorderBrush = on ? accent : new SolidColorBrush(Color.Parse("#303030"));
                setColor[i](on ? accent : new SolidColorBrush(Color.Parse("#4A4A4A")));
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
                    Points          = new Avalonia.Collections.AvaloniaList<Point>(WaveformIconPoints(i)),
                };
                var wc = new Canvas { Width = 26, Height = 12 };
                wc.Children.Add(poly);
                content = wc;
                setColor[i] = brush => poly.Stroke = brush;
            }
            else
            {
                var lbl = new TextBlock { Text = opts[i].ToUpperInvariant(), FontSize = 10 };
                content = lbl;
                setColor[i] = brush => lbl.Foreground = brush;
            }

            var cell = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Padding         = waveIcons ? new Thickness(5, 4) : new Thickness(8, 3),
                Margin          = new Thickness(1),
                Cursor          = new Cursor(StandardCursorType.Hand),
                Child           = content,
            };
            cell.PointerPressed += (_, _) =>
                param.Value = param.ParameterType == S1ParameterType.Toggle ? (idx > 0 ? 127 : 0) : idx;
            borders[i] = cell;
            row.Children.Add(cell);
        }

        Refresh(param.Value);
        param.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() => Refresh(v));

        return new StackPanel
        {
            Margin              = new Thickness(3, 2, 3, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                row,
                new TextBlock
                {
                    Text                = param.Name,
                    FontSize            = 9,
                    Foreground          = new SolidColorBrush(Color.Parse("#666666")),
                    Margin              = new Thickness(0, 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment       = TextAlignment.Center,
                },
            },
        };
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

        var canvas = new Canvas { Width = W, Height = H, Margin = new Thickness(4, 0, 2, 3) };
        canvas.Children.Add(fillPath);
        canvas.Children.Add(linePath);
        canvas.Children.Add(marker);

        void Update()
        {
            double fN = Math.Clamp(freqParam.Value / 127.0 + _filterModOffset, 0.0, 1.0);
            double rN = resParam.Value / 127.0;
            double xC = 8 + fN * (W - 16);

            const double flatTop = 3.0;
            const double flatBot = H - 2.0;
            const double slope   = 52.0;
            double h     = flatBot - flatTop;
            double peakY = Math.Max(0.5, flatTop - rN * h * 0.35);

            void Stroke(StreamGeometryContext ctx)
            {
                ctx.BeginFigure(new Point(0, flatTop), false);

                // ① Flat passband to just before cutoff
                double passEnd = Math.Max(0, xC - 8);
                if (passEnd > 0)
                    ctx.LineTo(new Point(passEnd, flatTop));

                // ② Resonance spike — rises above flatTop, arrives pointing straight down.
                //    When rN=0: peakY=flatTop → spike invisible, smooth join to rolloff.
                ctx.CubicBezierTo(
                    new Point(xC - 4, peakY),
                    new Point(xC,     peakY),
                    new Point(xC,     flatTop + h * 0.05));

                // ③ Convex quarter-arc rolloff — starts vertical, arrives horizontal.
                //    CP1 continues straight down (G1 smooth with ②).
                //    CP2 pulls hard left at flatBot → no S-curve, guaranteed convex.
                ctx.CubicBezierTo(
                    new Point(xC,              flatTop + h * 0.55),
                    new Point(xC + slope * 0.45, flatBot),
                    new Point(xC + slope,        flatBot));

                if (xC + slope < W)
                    ctx.LineTo(new Point(W, flatBot));
                ctx.EndFigure(false);
            }

            var sg = new StreamGeometry();
            using (var ctx = sg.Open()) Stroke(ctx);
            linePath.Data = sg;

            var fillSg = new StreamGeometry();
            using (var ctx = fillSg.Open())
            {
                Stroke(ctx);
                ctx.LineTo(new Point(0, H));
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

        var fillPoly   = new Polygon  { Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x70, 0xC8, 0x70)) };
        var strokePoly = new Polyline { Stroke = EnvAccent, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round };

        // Segment labels
        var lblA = new TextBlock { Text = "A", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };
        var lblD = new TextBlock { Text = "D", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };
        var lblS = new TextBlock { Text = "S", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };
        var lblR = new TextBlock { Text = "R", FontSize = 7, Foreground = new SolidColorBrush(Color.Parse("#506050")) };

        var canvas = new Canvas { Width = W, Height = H, Margin = new Thickness(4, 0, 2, 4) };
        canvas.Children.Add(fillPoly);
        canvas.Children.Add(strokePoly);
        canvas.Children.Add(lblA);
        canvas.Children.Add(lblD);
        canvas.Children.Add(lblS);
        canvas.Children.Add(lblR);

        void Update()
        {
            // All four segments share the canvas proportionally by their normalized values.
            // Sustain width scales with its level (same value drives both height and width).
            // Shape always fills W exactly.
            const double minSeg  = 5;
            const double varPool = W - 4 * minSeg;  // 232px variable pool

            double aN = attackP.Value  / 127.0;
            double dN = decayP.Value   / 127.0;
            double sN = sustainP.Value / 127.0;
            double rN = releaseP.Value / 127.0;
            double sum = aN + dN + sN + rN;
            if (sum < 0.01) { aN = dN = sN = rN = 0.25; sum = 1.0; }

            double aT = minSeg + (aN / sum) * varPool;
            double dT = minSeg + (dN / sum) * varPool;
            double hw = minSeg + (sN / sum) * varPool;
            double rT = minSeg + (rN / sum) * varPool;
            double sL = H - (sustainP.Value / 127.0) * (H - 5);

            var pts = new[]
            {
                new Point(0,                    H),
                new Point(aT,                   3),
                new Point(aT + dT,              sL),
                new Point(aT + dT + hw,         sL),
                new Point(aT + dT + hw + rT,    H),
            };

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
            if (!_filterModEnabled || _envPhase == EnvPhase.Off)
            {
                dot.IsVisible = false;
                return;
            }

            // Re-derive segment layout from current param values (mirrors Update() logic).
            double aN2  = attackP.Value  / 127.0;
            double dN2  = decayP.Value   / 127.0;
            double sN2  = sustainP.Value / 127.0;
            double rN2  = releaseP.Value / 127.0;
            double sum2 = aN2 + dN2 + sN2 + rN2;
            if (sum2 < 0.01) { aN2 = dN2 = sN2 = rN2 = 0.25; sum2 = 1.0; }
            const double minSeg2  = 5;
            const double varPool2 = W - 4 * minSeg2;
            double aT2 = minSeg2 + (aN2 / sum2) * varPool2;
            double dT2 = minSeg2 + (dN2 / sum2) * varPool2;
            double hw2 = minSeg2 + (sN2 / sum2) * varPool2;
            double rT2 = minSeg2 + (rN2 / sum2) * varPool2;
            double sL2 = H - sN2 * (H - 5);
            double lvl  = _envLevel;

            double dx, dy;
            switch (_envPhase)
            {
                case EnvPhase.Attack:
                    dx = lvl * aT2;
                    dy = H - lvl * (H - 3);
                    break;
                case EnvPhase.Decay:
                    double dd = 1.0 - sN2 > 0.001
                        ? Math.Clamp((1.0 - lvl) / (1.0 - sN2), 0, 1) : 1.0;
                    dx = aT2 + dd * dT2;
                    dy = 3 + dd * (sL2 - 3);
                    break;
                case EnvPhase.Sustain:
                    dx = aT2 + dT2;
                    dy = sL2;
                    break;
                case EnvPhase.Release:
                    double rp = _envLevelAtRelease > 0.001
                        ? Math.Clamp(1.0 - lvl / _envLevelAtRelease, 0, 1) : 1.0;
                    dx = aT2 + dT2 + hw2 + rp * rT2;
                    dy = sL2 + rp * (H - sL2);
                    break;
                default:
                    dot.IsVisible = false;
                    return;
            }

            dot.IsVisible = true;
            Canvas.SetLeft(dot, dx - 3.5);
            Canvas.SetTop(dot, dy - 3.5);
        }

        _envelopeDotUpdate = UpdateDot;
        Update();
        attackP.ValueChanged  += (_, _) => Dispatcher.UIThread.Post(Update);
        decayP.ValueChanged   += (_, _) => Dispatcher.UIThread.Post(Update);
        sustainP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        releaseP.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Update);

        return canvas;
    }

    // ── Chord voice row (LED toggle + semitone slider) ────────────────────────────

    private static Control MakeChordVoiceRow(
        int n, S1Parameter toggleParam, S1Parameter shiftParam, IBrush accent)
    {
        int   GetShift()       => Math.Clamp(shiftParam.Value - 64, -12, 12);
        string FormatSt(int s) => s > 0 ? $"+{s}" : s.ToString();

        var lbl = new TextBlock
        {
            Text              = $"V{n}",
            Width             = 14,
            FontSize          = 8,
            Foreground        = new SolidColorBrush(Color.Parse("#555555")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggleLbl = new TextBlock
        {
            Text              = "ON",
            FontSize          = 7.5,
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

        void RefreshToggle()
        {
            bool on = toggleParam.Value > 0;
            toggle.Background  = on ? new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) : new SolidColorBrush(Color.Parse("#191919"));
            toggle.BorderBrush = on ? accent : new SolidColorBrush(Color.Parse("#2E2E2E"));
            toggleLbl.Foreground = on ? accent : new SolidColorBrush(Color.Parse("#444444"));
        }
        RefreshToggle();
        toggle.PointerPressed += (_, _) => { toggleParam.Value = toggleParam.Value > 0 ? 0 : 127; };
        toggleParam.ValueChanged += (_, _) => Dispatcher.UIThread.Post(RefreshToggle);

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
            Text              = FormatSt(GetShift()),
            FontSize          = 8,
            Foreground        = new SolidColorBrush(Color.Parse("#888888")),
            Width             = 22,
            TextAlignment     = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        slider.ValueChanged += (_, e) =>
        {
            int s = (int)Math.Round(e.NewValue);
            shiftParam.Value = s + 64;
            valLbl.Text = FormatSt(s);
        };
        shiftParam.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() =>
        {
            int s = Math.Clamp(v - 64, -12, 12);
            slider.Value = s;
            valLbl.Text  = FormatSt(s);
        });

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

    // ── Drone latching button (sustain hold) ──────────────────────────────────────

    private static Control MakeDroneButton(S1Parameter param, IBrush accent)
    {
        var led = new Border
        {
            Width         = 5,
            Height        = 5,
            CornerRadius  = new CornerRadius(3),
            Margin        = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text          = "HOLD",
            FontSize      = 8.5,
            LetterSpacing = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var inner = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children          = { led, label },
        };

        var btn = new Border
        {
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(10, 4),
            Margin              = new Thickness(3, 2),
            Cursor              = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child               = inner,
        };
        ToolTip.SetTip(btn, "Click to latch / release held notes");

        void Refresh()
        {
            bool on           = param.Value > 0;
            var  col          = ((SolidColorBrush)accent).Color;
            btn.Background    = on ? new SolidColorBrush(Color.FromArgb(0x14, col.R, col.G, col.B))
                                   : new SolidColorBrush(Color.Parse("#161616"));
            btn.BorderBrush   = on ? accent : new SolidColorBrush(Color.Parse("#2E2E2E"));
            led.Background    = on ? accent : new SolidColorBrush(Color.Parse("#2A2A2A"));
            label.Foreground  = on ? accent : new SolidColorBrush(Color.Parse("#555555"));
        }
        Refresh();
        btn.PointerPressed       += (_, _) => { param.Value = param.Value > 0 ? 0 : 127; };
        param.ValueChanged       += (_, _) => Dispatcher.UIThread.Post(Refresh);

        return btn;
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

        for (int g = 0; g < 4; g++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text              = $"PTN {g + 1}",
                FontSize          = 9,
                Foreground        = new SolidColorBrush(Color.Parse("#505050")),
                Width             = 38,
                VerticalAlignment = VerticalAlignment.Center,
            });

            for (int p = 0; p < 16; p++)
            {
                int program = g * 16 + p;
                var btn = new Button { Content = (p + 1).ToString(), Classes = { "patch-btn" } };
                btn.Click += (_, _) => OnPatchClicked(program, btn);
                _patchButtons.Add(btn);
                row.Children.Add(btn);
            }

            rows.Children.Add(row);
        }

        PatchGridContainer.Children.Add(rows);
    }

    private void OnPatchClicked(int program, Button btn)
    {
        _patch.SendProgramChange(program);
        HighlightPatchButton(program);
    }

    private void HighlightPatchButton(int program)
    {
        foreach (var b in _patchButtons)
            b.Classes.Remove("patch-btn-active");
        if ((uint)program < (uint)_patchButtons.Count)
            _patchButtons[program].Classes.Add("patch-btn-active");

        if (_patternSync && !string.IsNullOrEmpty(_prmFolder))
            TryLoadPatternPrm(program);
    }

    private void PopulateSection(WrapPanel panel, IReadOnlyList<S1Parameter> parameters, IBrush accent)
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

    private static string GetKnobDisplayValue(S1Parameter param)
    {
        int cc  = param.CcNumber;
        int val = param.Value;

        return cc switch
        {
            76  => (val - 64).ToString(),
            77  => FormatSemitone(val - 64),
            102 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}",
            103 => Math.Min(200, (int)Math.Round(val * 255.0 / 127)).ToString(),
            104 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}",
            _   => PrmCcMap.ByCC.TryGetValue(cc, out var e) ? e.Info.ToPrm(val).ToString()
                                                             : val.ToString()
        };
    }

    private static string GetPrmDisplayString(PrmParameter p)
    {
        if (p.Options is not null)
            return p.Value < p.Options.Length ? p.Options[p.Value] : p.Value.ToString();
        int v = p.ToPrm();
        return p.PrmKey switch
        {
            "REVERB_PRE_DELAY"              => $"{v}ms",
            "LENG"                          => $"{v} steps",
            "RISER_RESO" or "RISER_LEVEL"   => $"{v}%",
            _                               => v.ToString(),
        };
    }

    private static Control MakeKnob(S1Parameter param, IBrush accent, int minCcValue = 0, double containerWidth = 68)
    {
        string initDisplay = GetKnobDisplayValue(param);

        var knob = new RotaryKnob { Value = param.Value, AccentBrush = accent };
        ToolTip.SetTip(knob, $"{param.Name}: {initDisplay}");

        var valueLabel = new TextBlock { Classes = { "param-value-label" }, Text = initDisplay };

        knob.ValueChanged += (_, v) =>
        {
            param.Value = Math.Max(minCcValue, v);
            string display = GetKnobDisplayValue(param);
            ToolTip.SetTip(knob, $"{param.Name}: {display}");
            valueLabel.Text = display;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                knob.Value = v;
                string display = GetKnobDisplayValue(param);
                ToolTip.SetTip(knob, $"{param.Name}: {display}");
                valueLabel.Text = display;
            });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        bool small = containerWidth <= 58;
        var container = new Grid
        {
            Width          = containerWidth,
            Margin         = new Thickness(small ? 2 : 3, 4),
            RowDefinitions = small
                ? new RowDefinitions("40,16,22")
                : new RowDefinitions("50,16,28"),
        };
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(knob,       0);
        Grid.SetRow(valueLabel, 1);
        Grid.SetRow(nameLabel,  2);
        container.Children.Add(knob);
        container.Children.Add(valueLabel);
        container.Children.Add(nameLabel);
        return container;
    }

    private static Control MakeDropdown(S1Parameter param)
    {
        var opts  = param.Options!;
        var combo = new ComboBox { Classes = { "param-combo" } };
        foreach (var opt in opts)
            combo.Items.Add(opt);
        combo.SelectedIndex = Math.Clamp(param.Value, 0, opts.Length - 1);

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                param.Value = combo.SelectedIndex;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                var idx = Math.Clamp(v, 0, opts.Length - 1);
                if (combo.SelectedIndex != idx)
                    combo.SelectedIndex = idx;
            });

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            = { combo, new TextBlock { Classes = { "param-label" }, Text = param.Name } },
        };
    }

    private static Control MakeToggle(S1Parameter param)
    {
        var cb = new CheckBox
        {
            Classes   = { "param-toggle" },
            Content   = param.Name,
            IsChecked = param.Value > 0,
        };

        cb.IsCheckedChanged += (_, _) =>
            param.Value = (cb.IsChecked == true) ? 127 : 0;

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() => cb.IsChecked = v > 0);

        return cb;
    }

    private static Control MakeKeyShiftSlider(S1Parameter param)
    {
        const int SemitoneMin = -12;
        const int SemitoneMax =  12;

        int   ToSemitone(int cc) => Math.Clamp(cc - 64, SemitoneMin, SemitoneMax);

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
            Text                = FormatSemitone(initSt),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        slider.ValueChanged += (_, e) =>
        {
            int s = (int)Math.Round(e.NewValue);
            param.Value     = s + 64;
            valueLabel.Text = FormatSemitone(s);
        };

        param.ValueChanged += (_, cc) =>
            Dispatcher.UIThread.Post(() =>
            {
                int s = ToSemitone(cc);
                slider.Value    = s;
                valueLabel.Text = FormatSemitone(s);
            });

        return new StackPanel
        {
            Spacing             = 3,
            Margin              = new Thickness(4, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children            = { slider, valueLabel, new TextBlock { Classes = { "param-label" }, Text = param.Name } },
        };
    }

    private void BuildEffectsPanel()
    {
        // Reverb + Delay side by side
        var sideBySide = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Margin            = new Thickness(0, 0, 0, 2),
        };
        var reverbCol = new StackPanel();
        reverbCol.Children.Add(MakeSubSectionHeader("REVERB", FxAccent));
        var revKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        revKnobs.Children.Add(MakeKnob(_patch.GetByCC(91)!, FxAccent));
        revKnobs.Children.Add(MakeKnob(_patch.GetByCC(89)!, FxAccent));
        reverbCol.Children.Add(revKnobs);

        var divider = new Border
        {
            Width      = 1,
            Background = new SolidColorBrush(Color.Parse("#2E2E2E")),
            Margin     = new Thickness(2, 0),
        };

        var delayCol = new StackPanel();
        delayCol.Children.Add(MakeSubSectionHeader("DELAY", FxAccent));
        var delKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        delKnobs.Children.Add(MakeKnob(_patch.GetByCC(92)!, FxAccent));
        delKnobs.Children.Add(BuildDelayTimeKnob());
        delayCol.Children.Add(delKnobs);

        Grid.SetColumn(reverbCol, 0);
        Grid.SetColumn(divider,   1);
        Grid.SetColumn(delayCol,  2);
        sideBySide.Children.Add(reverbCol);
        sideBySide.Children.Add(divider);
        sideBySide.Children.Add(delayCol);
        EffectsPanel.Children.Add(sideBySide);

        EffectsPanel.Children.Add(MakeSubSectionHeader("CHORUS", FxAccent));
        EffectsPanel.Children.Add(MakeLedButtonGroup(_patch.GetByCC(93)!, FxAccent));
    }

    private Control BuildDelayTimeKnob()
    {
        var param   = _patch.GetByCC(90)!;
        var delaySw = _prmDelayMain[0];

        string GetDisplay()
        {
            if (delaySw.Value != 1)
                return $"{1 + (int)Math.Round(param.Value * 739.0 / 127)}ms";
            var opts = _delayTempo.Options!;
            int idx  = Math.Clamp(param.Value, 0, opts.Length - 1);  // CC 0-15 → index 0-15
            return opts[idx];
        }

        var knob       = new RotaryKnob { Value = param.Value, AccentBrush = FxAccent };
        var valueLabel = new TextBlock   { Classes = { "param-value-label" }, Text = GetDisplay() };
        ToolTip.SetTip(knob, $"{param.Name}: {GetDisplay()}");

        void Refresh()
        {
            string d = GetDisplay();
            ToolTip.SetTip(knob, $"{param.Name}: {d}");
            valueLabel.Text = d;
        }

        // When sync is on, CC 0-15 maps evenly across the full knob rotation.
        int SyncToKnob(int cc)   => (int)Math.Round(Math.Clamp(cc, 0, 15) * 127.0 / 15);
        int KnobToSync(int knob) => Math.Clamp((int)Math.Round(knob * 15.0 / 127), 0, 15);

        knob.ValueChanged    += (_, v) => { param.Value = delaySw.Value == 1 ? KnobToSync(v) : v; Refresh(); };
        param.ValueChanged   += (_, v) => Dispatcher.UIThread.Post(() => { knob.Value = delaySw.Value == 1 ? SyncToKnob(v) : v; Refresh(); });
        delaySw.ValueChanged += (_, sw) => Dispatcher.UIThread.Post(() => { knob.Value = sw == 1 ? SyncToKnob(param.Value) : param.Value; Refresh(); });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        var container = new Grid
        {
            Width          = 68,
            Margin         = new Thickness(3, 4),
            RowDefinitions = new RowDefinitions("56,14,26"),
        };
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(knob,       0);
        Grid.SetRow(valueLabel, 1);
        Grid.SetRow(nameLabel,  2);
        container.Children.Add(knob);
        container.Children.Add(valueLabel);
        container.Children.Add(nameLabel);
        return container;
    }

    // ── LFO Rate knob — context-aware: free 0-127 when sync off, 32 values when sync on ──
    private Control MakeLfoRateKnob()
    {
        var param  = _patch.GetByCC(3)!;
        var syncSw = _patch.GetByCC(106)!;

        string GetDisplay()
        {
            if (syncSw.Value == 0) return ((int)Math.Round(param.Value * 255.0 / 127)).ToString();
            int idx = Math.Clamp(param.Value, 0, s_lfoSyncValues.Length - 1);
            return s_lfoSyncValues[idx];
        }

        var knob       = new RotaryKnob { Value = param.Value, AccentBrush = LfoAccent };
        var valueLabel = new TextBlock   { Classes = { "param-value-label" }, Text = GetDisplay() };
        ToolTip.SetTip(knob, $"{param.Name}: {GetDisplay()}");

        void Refresh()
        {
            string d = GetDisplay();
            ToolTip.SetTip(knob, $"{param.Name}: {d}");
            valueLabel.Text = d;
        }

        // When sync on, CC 0–30 spread evenly across full knob rotation.
        int SyncToKnob(int cc)   => (int)Math.Round(Math.Clamp(cc, 0, 30) * 127.0 / 30);
        int KnobToSync(int knob) => Math.Clamp((int)Math.Round(knob * 30.0 / 127), 0, 30);

        knob.ValueChanged   += (_, v) => { param.Value = syncSw.Value == 1 ? KnobToSync(v) : v; Refresh(); };
        param.ValueChanged  += (_, v) => Dispatcher.UIThread.Post(() => { knob.Value = syncSw.Value == 1 ? SyncToKnob(v) : v; Refresh(); });
        syncSw.ValueChanged += (_, sw) => Dispatcher.UIThread.Post(() => { knob.Value = sw == 1 ? SyncToKnob(param.Value) : param.Value; Refresh(); });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        var container = new Grid
        {
            Width          = 68,
            Margin         = new Thickness(3, 4),
            RowDefinitions = new RowDefinitions("56,14,26"),
        };
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(knob,       0);
        Grid.SetRow(valueLabel, 1);
        Grid.SetRow(nameLabel,  2);
        container.Children.Add(knob);
        container.Children.Add(valueLabel);
        container.Children.Add(nameLabel);
        return container;
    }

    // ── Tab 2: PRM Viewer ─────────────────────────────────────────────────────

    private void BuildPrmViewerContent()
    {
        // ── Column 0: OSCILLATOR (spans all rows) ─────────────────────────
        var oscCard = MakeSectionCard("OSCILLATOR", OscAccent, out var oscContent);

        var mainOscForViewer = _patch.Oscillator
            .Where(p => p.CcNumber != 102 && p.CcNumber != 103 && p.CcNumber != 104 && p.CcNumber != 107)
            .ToList();
        foreach (var p in mainOscForViewer)
            oscContent.Children.Add(MakePrmViewerCcRow(p));

        oscContent.Children.Add(MakeSubSectionHeader("OSC DRAW", OscAccent));
        oscContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(102)!));  // Draw Multiply
        oscContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(107)!));  // Draw Step/Slope
        oscContent.Children.Add(new Viewbox
        {
            Stretch   = Stretch.Uniform,
            MaxHeight = 60,
            Margin    = new Thickness(0, 4, 0, 4),
            Child     = MakeDrawBarsControl(),
        });

        oscContent.Children.Add(MakeSubSectionHeader("OSC CHOP", OscAccent));
        oscContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(103)!));  // Chop Overtone
        oscContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(104)!));  // Chop Comb
        oscContent.Children.Add(MakePrmInfoRow(_prmChopType));
        oscContent.Children.Add(MakePrmInfoRow(_prmChopCombType));
        oscContent.Children.Add(MakeChopPatternControl());

        var riserCard = MakeSectionCard("RISER", FxAccent, out var riserContent);
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserSw));
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserMode));
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserCtrl));
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserBeat));
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserShape));
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserReso));
        riserContent.Children.Add(MakePrmInfoRow(_prmRiserLevel));

        var col0Stack = new StackPanel { Spacing = 4 };
        col0Stack.Children.Add(oscCard);
        Grid.SetColumn(col0Stack, 0); Grid.SetRow(col0Stack, 0); Grid.SetRowSpan(col0Stack, 3);
        PrmViewerGrid.Children.Add(col0Stack);

        // ── Column 1: FILTER / ENVELOPE / LFO ────────────────────────────
        var filterCard = MakeSectionCard("FILTER", FiltAccent, out var filterContent);
        var filterParams = _patch.Filter.ToList();
        var filterGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, *"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", (filterParams.Count + 1) / 2))),
            RowSpacing        = 2,
        };
        for (int i = 0; i < filterParams.Count; i++)
        {
            var ctrl = MakePrmViewerCcRowCompact(filterParams[i]);
            Grid.SetRow(ctrl, i / 2);
            Grid.SetColumn(ctrl, i % 2);
            filterGrid.Children.Add(ctrl);
        }
        filterContent.Children.Add(filterGrid);

        Grid.SetColumn(filterCard, 1); Grid.SetRow(filterCard, 0);
        PrmViewerGrid.Children.Add(filterCard);

        var envCard = MakeSectionCard("ENVELOPE", EnvAccent, out var envContent);
        foreach (var p in _patch.Envelope)
            envContent.Children.Add(MakePrmViewerCcRow(p));
        Grid.SetColumn(envCard, 1); Grid.SetRow(envCard, 1);
        PrmViewerGrid.Children.Add(envCard);

        var lfoCard = MakeSectionCard("LFO", LfoAccent, out var lfoContent);
        foreach (var p in _patch.Lfo)
            lfoContent.Children.Add(MakePrmViewerCcRow(p));
        var col1Row2Stack = new StackPanel { Spacing = 4 };
        col1Row2Stack.Children.Add(lfoCard);
        col1Row2Stack.Children.Add(riserCard);
        Grid.SetColumn(col1Row2Stack, 1); Grid.SetRow(col1Row2Stack, 2);
        PrmViewerGrid.Children.Add(col1Row2Stack);

        // ── Column 2: EFFECTS / VOICE ─────────────────────────────────────
        var fxCard = MakeSectionCard("EFFECTS", FxAccent, out var fxContent);

        // Reverb
        fxContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(91)!));  // Reverb Level
        fxContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(89)!));  // Reverb Time
        foreach (var p in _prmReverbMain) fxContent.Children.Add(MakePrmInfoRow(p));
        foreach (var p in _prmReverbAdv)  fxContent.Children.Add(MakePrmInfoRow(p));

        // Delay
        fxContent.Children.Add(MakeSubSectionHeader("DELAY", FxAccent));
        fxContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(92)!));  // Delay Level
        fxContent.Children.Add(MakeDelayTimeViewerRow());
        foreach (var p in _prmDelayMain) fxContent.Children.Add(MakePrmInfoRow(p));
        fxContent.Children.Add(MakePrmInfoRow(_delayTempo));
        foreach (var p in _prmDelayAdv)  fxContent.Children.Add(MakePrmInfoRow(p));

        // Chorus
        fxContent.Children.Add(MakeSubSectionHeader("CHORUS", FxAccent));
        fxContent.Children.Add(MakePrmViewerCcRow(_patch.GetByCC(93)!));  // Chorus Type

        Grid.SetColumn(fxCard, 2); Grid.SetRow(fxCard, 0);
        PrmViewerGrid.Children.Add(fxCard);

        var voiceCard = MakeSectionCard("VOICE", VoiceAccent, out var voiceContent);
        foreach (var p in _patch.Controls) voiceContent.Children.Add(MakePrmViewerCcRow(p));
        foreach (var p in _patch.Voice)    voiceContent.Children.Add(MakePrmViewerCcRow(p));

        Grid.SetColumn(voiceCard, 2); Grid.SetRow(voiceCard, 1); Grid.SetRowSpan(voiceCard, 2);
        PrmViewerGrid.Children.Add(voiceCard);

        // ── Row 3: SEQUENCER (full width) ────────────────────────────────
        var seqCard = MakeSectionCard("SEQUENCER", SeqAccent, out var seqContent);

        // PATTERN / ARPEGGIATOR / MOTION ASSIGN as three side-by-side columns
        var seqMetaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto, Auto, *, Auto"),
            Margin            = new Thickness(0, 0, 0, 4),
        };

        var patCol = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 20, 0) };
        patCol.Children.Add(MakeSubSectionHeader("PATTERN", SeqAccent));
        patCol.Children.Add(MakeInfoRow("Tempo:",     _tempoLabel));
        patCol.Children.Add(MakeInfoRow("Transpose:", _transposeLabel));
        patCol.Children.Add(MakePrmInfoRow(_prmLeng));
        patCol.Children.Add(MakePrmInfoRow(_prmShuffle));
        patCol.Children.Add(MakePrmInfoRow(_prmLevel));
        patCol.Children.Add(MakePrmInfoRow(_prmScale));
        patCol.Children.Add(MakePrmInfoRow(_prmTempoSync));
        Grid.SetColumn(patCol, 0);
        seqMetaGrid.Children.Add(patCol);

        var arpCol = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 20, 0) };
        arpCol.Children.Add(MakeSubSectionHeader("ARPEGGIATOR", SeqAccent));
        arpCol.Children.Add(MakePrmInfoRow(_prmArpType));
        arpCol.Children.Add(MakePrmInfoRow(_prmArpRate));
        Grid.SetColumn(arpCol, 1);
        seqMetaGrid.Children.Add(arpCol);

        var motCol = new StackPanel { Spacing = 2 };
        motCol.Children.Add(MakeSubSectionHeader("MOTION ASSIGN", SeqAccent));
        for (int i = 0; i < 8; i++)
            motCol.Children.Add(MakeInfoRow($"Lane {i + 1}:", _motionCcLabels[i]));
        Grid.SetColumn(motCol, 2);
        seqMetaGrid.Children.Add(motCol);

        var dmCol = new StackPanel { Spacing = 2, Margin = new Thickness(20, 0, 0, 0) };
        dmCol.Children.Add(MakeSubSectionHeader("D-MOTION", DmAccent));
        dmCol.Children.Add(MakePrmInfoRow(_prmDmAssignX));
        dmCol.Children.Add(MakePrmInfoRow(_prmDmAssignY));
        dmCol.Children.Add(MakePrmInfoRow(_prmDmAssignTap));
        dmCol.Children.Add(MakePrmInfoRow(_prmDmAssignFf));
        dmCol.Children.Add(MakePrmInfoRow(_prmDmSensX));
        dmCol.Children.Add(MakePrmInfoRow(_prmDmSensY));
        Grid.SetColumn(dmCol, 3);
        seqMetaGrid.Children.Add(dmCol);

        seqContent.Children.Add(seqMetaGrid);
        seqContent.Children.Add(MakeSubSectionHeader("STEPS", SeqAccent));
        seqContent.Children.Add(MakeSequencerControl());

        Grid.SetColumn(seqCard, 0); Grid.SetRow(seqCard, 3); Grid.SetColumnSpan(seqCard, 3);
        PrmViewerGrid.Children.Add(seqCard);
    }

    // Creates a section card (Border + title + content StackPanel).
    private static Border MakeSectionCard(string title, IBrush accent, out StackPanel contentPanel)
    {
        contentPanel = new StackPanel { Spacing = 2 };
        return new Border
        {
            Classes = { "section-card" },
            Child   = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { "section-title" }, Text = title, Foreground = accent },
                    contentPanel,
                },
            },
        };
    }

    // Small divider + bold sub-section label for use inside section cards.
    private static StackPanel MakeSubSectionHeader(string title, IBrush accent) => new()
    {
        Margin   = new Thickness(0, 8, 0, 4),
        Children =
        {
            new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#444444")) },
            new TextBlock
            {
                Text       = title,
                FontSize   = 9.5,
                FontWeight = FontWeight.Bold,
                Foreground = accent,
                Margin     = new Thickness(0, 4, 0, 0),
            },
        },
    };

    // Read-only labeled row for a CC-mapped parameter; subscribes to ValueChanged.
    private static Control MakePrmViewerCcRow(S1Parameter param)
    {
        var lbl = new TextBlock
        {
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            Text       = GetPrmViewerCcValue(param),
        };
        param.ValueChanged += (_, _) => Dispatcher.UIThread.Post(() => lbl.Text = GetPrmViewerCcValue(param));
        return MakeInfoRow(param.Name + ":", lbl);
    }

    private static string GetPrmViewerCcValue(S1Parameter param)
    {
        if (param.Options is not null)
        {
            int idx = Math.Clamp(param.Value, 0, param.Options.Length - 1);
            return param.Options[idx];
        }
        return param.ParameterType switch
        {
            S1ParameterType.Toggle        => param.Value > 0 ? "On" : "Off",
            S1ParameterType.BipolarSlider => FormatSemitone(Math.Clamp(param.Value - 64, -12, 12)),
            _                             => GetKnobDisplayValue(param),
        };
    }

    private static string FormatSemitone(int st) => st > 0 ? $"+{st}" : st.ToString();

    // Contextual Delay Time row for the PRM viewer.
    private Control MakeDelayTimeViewerRow()
    {
        var delaySw     = _prmDelayMain[0];
        var delayTimeCC = _patch.GetByCC(90)!;

        var lbl = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")) };

        void Refresh()
        {
            lbl.Text = delaySw.Value == 0
                ? $"{1 + (int)Math.Round(delayTimeCC.Value * 739.0 / 127)}ms"
                : GetPrmDisplayString(_delayTempo);
        }

        Refresh();
        delaySw.ValueChanged     += (_, _) => Dispatcher.UIThread.Post(Refresh);
        delayTimeCC.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _delayTempo.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);

        return MakeInfoRow("Delay Time:", lbl);
    }

    // 16-bar bipolar draw waveform display (lo byte first, then hi byte per PRM value).
    private Panel MakeDrawBarsControl()
    {
        const double CellH = 22.0;
        const double BarW  = 18.75;

        var posBars   = new Border[16];
        var negBars   = new Border[16];
        var valLabels = new TextBlock[16];

        void UpdateBars()
        {
            for (int pt = 0; pt < DrawWave.Points; pt++)
            {
                int signed = _drawWave.GetPoint(pt);
                int raw    = signed < 0 ? signed + 65536 : signed;
                int lo     = raw & 0xFF;
                int hi     = (raw >> 8) & 0xFF;
                int[] pads = { lo > 127 ? lo - 256 : lo, hi > 127 ? hi - 256 : hi };

                for (int b = 0; b < 2; b++)
                {
                    int idx = pt * 2 + b;
                    int v   = pads[b];
                    posBars[idx].Height = v > 0 ? Math.Max(1, v  / 100.0 * CellH) : 0;
                    negBars[idx].Height = v < 0 ? Math.Max(1, -v / 100.0 * CellH) : 0;
                    valLabels[idx].Text = v.ToString();
                }
            }
        }

        var barsRow   = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var labelsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 2, 0, 0) };

        for (int i = 0; i < 16; i++)
        {
            var posBar = new Border { Width = BarW, Height = 0, Background = OscAccent,
                                      VerticalAlignment = VerticalAlignment.Bottom };
            var negBar = new Border { Width = BarW, Height = 0, Background = OscAccent,
                                      VerticalAlignment = VerticalAlignment.Top };
            posBars[i] = posBar;
            negBars[i] = negBar;

            var posCell = new Grid { Height = CellH };
            posCell.Children.Add(posBar);
            var negCell = new Grid { Height = CellH };
            negCell.Children.Add(negBar);

            barsRow.Children.Add(new StackPanel
            {
                Width    = BarW,
                Children =
                {
                    posCell,
                    new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#444444")) },
                    negCell,
                },
            });

            var lbl = new TextBlock
            {
                FontSize      = 8,
                Width         = BarW,
                Foreground    = new SolidColorBrush(Color.Parse("#AAAAAA")),
                Text          = "0",
                TextAlignment = TextAlignment.Center,
            };
            valLabels[i] = lbl;
            labelsRow.Children.Add(lbl);
        }

        UpdateBars();
        _drawWave.PointsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateBars);

        return new StackPanel { Children = { barsRow, labelsRow } };
    }

    // Chop step-pattern grid display.
    private Panel MakeChopPatternControl()
    {
        var grid = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 2) };

        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            int waveform    = w;
            var stepSquares = new Border[ChopPattern.Steps];

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            row.Children.Add(new TextBlock
            {
                Text              = ChopPattern.WaveformNames[w],
                Width             = 42,
                FontSize          = 10,
                Foreground        = new SolidColorBrush(Color.Parse("#BBBBBB")),
                VerticalAlignment = VerticalAlignment.Center,
            });

            for (int s = 0; s < ChopPattern.Steps; s++)
            {
                var sq = new Border
                {
                    Width           = 16,
                    Height          = 16,
                    Margin          = new Thickness(1, 0),
                    Background      = _chopPattern.GetStep(w, s) ? s_chopOnBrush : s_chopOffBrush,
                    BorderBrush     = s_chopBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(2),
                };
                stepSquares[s] = sq;
                row.Children.Add(sq);
            }

            _chopPattern.PatternChanged += changedWaveform =>
            {
                if (changedWaveform != waveform) return;
                Dispatcher.UIThread.Post(() =>
                {
                    for (int s = 0; s < ChopPattern.Steps; s++)
                        stepSquares[s].Background = _chopPattern.GetStep(waveform, s)
                            ? s_chopOnBrush : s_chopOffBrush;
                });
            };

            grid.Children.Add(row);
        }

        return grid;
    }

    // ── Sequencer piano-roll ──────────────────────────────────────────────────

    private static readonly bool[] s_isBlackKey =
        { false, true, false, true, false, false, true, false, true, false, true, false };

    private static readonly IBrush[] s_voiceColors =
    {
        new SolidColorBrush(Color.Parse("#F0A040")),  // V1 orange
        new SolidColorBrush(Color.Parse("#40B0F0")),  // V2 blue
        new SolidColorBrush(Color.Parse("#70C870")),  // V3 green
        new SolidColorBrush(Color.Parse("#B070D8")),  // V4 purple
    };

    private Control MakeSequencerControl()
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

            int count = _sequence.StepCount;
            infoLabel.Text =
                $"Steps: {count}   " +
                $"Tempo: {_sequence.Tempo / 100.0:F1} BPM   " +
                $"Transpose: {_sequence.Transpose}   " +
                $"Shuffle: {_sequence.Shuffle}";

            // Auto-detect pitch range from active notes
            int minNote = 127, maxNote = 0;
            for (int s = 0; s < count; s++)
                foreach (var n in _sequence.Steps[s].Notes)
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
            for (int s = 0; s <= count; s++)
            {
                if (s % 4 != 0) continue;
                var line = new Border
                {
                    Width      = 1,
                    Height     = totalH,
                    Background = new SolidColorBrush(Color.Parse(s % 16 == 0 ? "#484848" : "#282828")),
                };
                Canvas.SetLeft(line, LabelW + s * ColW);
                Canvas.SetTop(line,  0);
                canvas.Children.Add(line);
            }

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
                var step = _sequence.Steps[s];
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
                    if (_sequence.Steps[s].Motions[m] != -1) { hasData = true; break; }
                if (!hasData) continue;

                int    slot      = m;
                string laneLabel = _sequence.MotionCCs[m] >= 0
                    ? $"CC{_sequence.MotionCCs[m]}" : $"M{m + 1}";

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
                    int mv = _sequence.Steps[s].Motions[slot];
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

                for (int s = 0; s <= count; s++)
                {
                    if (s % 4 != 0) continue;
                    var ln = new Border
                    {
                        Width      = 1,
                        Height     = LaneH,
                        Background = new SolidColorBrush(Color.Parse(s % 16 == 0 ? "#484848" : "#282828")),
                    };
                    Canvas.SetLeft(ln, LabelW + s * ColW); Canvas.SetTop(ln, 0);
                    lc.Children.Add(ln);
                }

                tempLanes.Add(lc);
            }

            // Pitch-bend lane
            bool hasPb = false;
            for (int s = 0; s < count; s++)
                if (_sequence.Steps[s].PitchBend != -32768) { hasPb = true; break; }

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
                    int    pb   = _sequence.Steps[s].PitchBend;
                    if (pb == -32768) continue;
                    double norm = Math.Clamp(pb / 32767.0, -1.0, 1.0);
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

                for (int s = 0; s <= count; s++)
                {
                    if (s % 4 != 0) continue;
                    var ln = new Border
                    {
                        Width      = 1,
                        Height     = LaneH,
                        Background = new SolidColorBrush(Color.Parse(s % 16 == 0 ? "#484848" : "#282828")),
                    };
                    Canvas.SetLeft(ln, LabelW + s * ColW); Canvas.SetTop(ln, 0);
                    lc.Children.Add(ln);
                }

                tempLanes.Add(lc);
            }

            if (tempLanes.Count > 0)
            {
                motionLanes.Children.Add(new TextBlock
                {
                    Text       = "MOTION",
                    FontSize   = 8,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#606060")),
                    Margin     = new Thickness(0, 6, 0, 2),
                });
                foreach (var lc in tempLanes)
                    motionLanes.Children.Add(lc);
            }
        }

        Rebuild();
        _sequence.DataChanged += (_, _) => Dispatcher.UIThread.Post(Rebuild);

        return new StackPanel
        {
            Children =
            {
                infoLabel,
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content                       = new StackPanel { Children = { canvas, motionLanes } },
                },
            },
        };
    }

    // ── Shared info-row helpers ───────────────────────────────────────────────

    private static StackPanel MakeInfoRow(string label, TextBlock valueLabel, double labelMinWidth = 130) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing     = 6,
        Children    =
        {
            new TextBlock
            {
                Text       = label,
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.Parse("#777777")),
                MinWidth   = labelMinWidth,
            },
            valueLabel,
        },
    };

    private static StackPanel MakePrmInfoRow(PrmParameter p)
    {
        var lbl = new TextBlock
        {
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            Text       = GetPrmDisplayString(p),
        };
        p.ValueChanged += (_, _) => Dispatcher.UIThread.Post(() => lbl.Text = GetPrmDisplayString(p));
        return MakeInfoRow(p.Name + ":", lbl);
    }

    private static Control MakePrmViewerCcRowCompact(S1Parameter param)
    {
        var lbl = new TextBlock
        {
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            Text       = GetPrmViewerCcValue(param),
        };
        param.ValueChanged += (_, _) => Dispatcher.UIThread.Post(() => lbl.Text = GetPrmViewerCcValue(param));
        return MakeInfoRow(param.Name + ":", lbl, labelMinWidth: 80);
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private async void OnConnectClicked(object? sender, RoutedEventArgs e) => await PerformConnectAsync();

    private async Task PerformConnectAsync()
    {
        if (DeviceCombo.SelectedIndex < 0)
        {
            SetStatus("Select a MIDI output device first.", "#FF6B6B");
            return;
        }

        var outPorts = new List<IMidiPortDetails>(_midi.Outputs);
        var outPort  = outPorts[DeviceCombo.SelectedIndex];

        try
        {
            var output = await _midi.OpenOutputAsync(outPort.Id);
            _patch.SetTransport(new ManagedMidiTransport(output), channel: MidiChannel);
        }
        catch (Exception ex)
        {
            SetStatus($"Output error: {ex.Message}", "#FF6B6B");
            return;
        }

        ConnectButton.Content        = "Reconnect";
        SendAllButton.IsEnabled      = true;
        PatchGridContainer.IsEnabled = true;

        if (InputCombo.SelectedIndex < 0 || InputCombo.SelectedIndex >= _inputDevices.Count)
        {
            SetStatus($"→ {outPort.Name}  (no input selected)", "#70C870");
            return;
        }

        try
        {
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

    private async void TryAutoConnect()
    {
        var outPorts = new List<IMidiPortDetails>(_midi.Outputs);
        int outIdx   = outPorts.FindIndex(p => p.Name.Contains("S-1", StringComparison.OrdinalIgnoreCase));
        if (outIdx < 0)
        {
            SetStatus("Auto-connect: S-1 output not found.", "#F0A040");
            return;
        }

        int inIdx = _inputDevices.FindIndex(d => d.Name.Contains("S-1", StringComparison.OrdinalIgnoreCase));

        DeviceCombo.SelectedIndex = outIdx;
        if (inIdx >= 0) InputCombo.SelectedIndex = inIdx;

        await PerformConnectAsync();
    }

    private void LoadSettings()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("autoConnect", out var el))
                _autoConnect = el.GetBoolean();
            if (doc.RootElement.TryGetProperty("filterModEnabled", out var el2))
                _filterModEnabled = el2.GetBoolean();
            if (doc.RootElement.TryGetProperty("prmFolder", out var el3))
                _prmFolder = el3.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("patternSync", out var el4))
                _patternSync = el4.GetBoolean();
        }
        catch { }
    }

    private void SaveSettings()
    {
        try { System.IO.File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { autoConnect = _autoConnect, filterModEnabled = _filterModEnabled, prmFolder = _prmFolder, patternSync = _patternSync }, JsonOptions)); }
        catch { }
    }

    private void OnMidiEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        switch (e.Event)
        {
            case ControlChangeEvent cc when (int)cc.Channel == MidiChannel - 1:
                _patch.HandleIncomingCC((int)cc.ControlNumber, (int)cc.ControlValue);
                break;
            case NoteOnEvent noteOn when (int)noteOn.Channel == MidiChannel - 1:
                if (noteOn.Velocity > 0)
                    Dispatcher.UIThread.Post(OnNoteOn);
                else
                    Dispatcher.UIThread.Post(OnNoteOff);
                break;
            case NoteOffEvent noteOff when (int)noteOff.Channel == MidiChannel - 1:
                Dispatcher.UIThread.Post(OnNoteOff);
                break;
            case ProgramChangeEvent pc when (int)pc.Channel == MidiChannel - 1:
                Dispatcher.UIThread.Post(() => HighlightPatchButton((int)pc.ProgramNumber));
                break;
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

        PresetNameBox.Text = preset.Name;
        _patch.LoadPreset(preset);

        if (preset.PrmOnly.Count > 0)
        {
            var prmLookup = AllPrmOnlyParams().ToDictionary(p => p.PrmKey);
            foreach (var entry in preset.PrmOnly)
            {
                if (prmLookup.TryGetValue(entry.PrmKey, out var prm))
                    prm.Value = entry.Value;
            }
        }

        await _patch.SendAllAsync();
        SetStatus($"Loaded: {preset.Name}", "#70C870");
    }

    // ── Pattern Sync ──────────────────────────────────────────────────────────

    private string PrmFileForProgram(int program)
    {
        int bank    = program / 16 + 1;
        int pattern = program % 16 + 1;
        return System.IO.Path.Combine(_prmFolder, $"S1_PTN{bank}-{pattern:D2}.PRM");
    }

    private void TryLoadPatternPrm(int program)
    {
        string path = PrmFileForProgram(program);
        if (!System.IO.File.Exists(path))
        {
            SetStatus($"Pattern Sync: {System.IO.Path.GetFileName(path)} not found in PRM folder", "#F0A040");
            return;
        }

        PrmFileData parsed;
        try { parsed = PrmFileParser.Parse(path); }
        catch (Exception ex)
        {
            SetStatus($"Pattern Sync: parse error — {ex.Message}", "#FF6B6B");
            return;
        }

        ApplyPrmData(parsed);
        SetStatus($"Pattern Sync: {System.IO.Path.GetFileName(path)}", "#888888");
    }

    private async Task<bool> ShowPatternSyncWarningAsync()
    {
        bool confirmed = false;

        var yesBtn = new Button { Content = "Enable Pattern Sync", Classes = { "toolbar" } };
        var noBtn  = new Button { Content = "Cancel",              Classes = { "toolbar" } };

        var dlg = new Window
        {
            Title                 = "Pattern Sync — Safety Warning",
            Width                 = 480,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = new SolidColorBrush(Color.Parse("#1C1C1C")),
            Content = new StackPanel
            {
                Margin   = new Thickness(24, 20),
                Spacing  = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text       = "⚠  EXPERIMENTAL FEATURE",
                        FontSize   = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#F0A040")),
                    },
                    new TextBlock
                    {
                        FontSize     = 11,
                        Foreground   = new SolidColorBrush(Color.Parse("#CCCCCC")),
                        TextWrapping = TextWrapping.Wrap,
                        Text         =
                            "When Pattern Sync is enabled, switching patterns — either by clicking " +
                            "a pattern button in the editor or by pressing a pattern on the S-1 — " +
                            "will automatically load the corresponding PRM file from your PRM Folder " +
                            "and update all editor values and the PRM Viewer.\n\n" +
                            "No values are sent back to the device during a pattern switch. " +
                            "The editor is updated only.",
                    },
                    new Border
                    {
                        Height     = 1,
                        Background = new SolidColorBrush(Color.Parse("#444444")),
                    },
                    new TextBlock
                    {
                        FontSize     = 10,
                        Foreground   = new SolidColorBrush(Color.Parse("#FF8844")),
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight   = FontWeight.SemiBold,
                        Text         =
                            "It is your responsibility to ensure your PRM folder is in sync with " +
                            "the patterns stored on the device. If the files do not match what is " +
                            "on the S-1, the editor will display incorrect values.",
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing     = 8,
                        Children    = { yesBtn, noBtn },
                    },
                },
            },
        };

        yesBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };
        noBtn.Click  += (_, _) => dlg.Close();

        await dlg.ShowDialog(this);
        return confirmed;
    }

    // ── PRM folder ────────────────────────────────────────────────────────────

    private async void OnBrowsePrmFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Select PRM Files Folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;

        _prmFolder        = folders[0].TryGetLocalPath() ?? "";
        PrmFolderBox.Text = _prmFolder;
        PatternSyncToggle.IsEnabled = !string.IsNullOrEmpty(_prmFolder);
        SaveSettings();
    }

    // ── PRM file open ─────────────────────────────────────────────────────────

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

        ApplyPrmData(parsed);

        var fileName = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
        PresetNameBox.Text = fileName;

        SetStatus("Sending PRM values…", "#AAAAAA");
        await _patch.SendAllAsync();
        SetStatus($"Loaded PRM: {fileName}", "#70C870");
    }

    private void ApplyInitPatch()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("InitPatch.prm")!;
        using var reader = new System.IO.StreamReader(stream);
        ApplyPrmData(PrmFileParser.Parse(reader));
    }

    private void ApplyPrmData(PrmFileData data)
    {
        foreach (var (key, rawValue) in data.Parameters)
        {
            if (!PrmCcMap.Map.TryGetValue(key, out var info)) continue;
            if (!int.TryParse(rawValue, out int prmValue)) continue;
            _patch.HandleIncomingCC(info.Cc, info.ToCc(prmValue));
        }

        _patch.HandleIncomingCC(1,  0);    // Mod Wheel = 0
        _patch.HandleIncomingCC(11, 127);  // Expression = 127

        LoadPrmOnly(data, _prmDelayMain);
        LoadPrmOnly(data, [_delayTempo]);
        LoadPrmOnly(data, _prmReverbMain);
        LoadPrmOnly(data, _prmDelayAdv);
        LoadPrmOnly(data, _prmReverbAdv);

        if (data.Parameters.TryGetValue("OSC_CHOP_COMB", out var combRaw) &&
            int.TryParse(combRaw, out int combPrm))
        {
            int combCc = Math.Clamp(3 + (int)Math.Round((combPrm - 1) * 124.0 / 31.0), 3, 127);
            _patch.HandleIncomingCC(104, combCc);
        }

        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            if (data.Parameters.TryGetValue(ChopPattern.PrmKeys[w], out var rawStr) &&
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

        // OSC chop extras
        LoadPrmOnly(data, new[] { _prmChopType, _prmChopCombType });

        // Pattern / Arpeggiator / Riser / D-Motion
        LoadPrmOnly(data, new[] { _prmLeng, _prmShuffle, _prmLevel, _prmScale, _prmTempoSync,
                                   _prmArpType, _prmArpRate });
        LoadPrmOnly(data, new[] { _prmRiserSw, _prmRiserMode, _prmRiserCtrl, _prmRiserBeat,
                                   _prmRiserShape, _prmRiserReso, _prmRiserLevel });
        LoadPrmOnly(data, new[] { _prmDmAssignX, _prmDmAssignY, _prmDmAssignTap, _prmDmAssignFf,
                                   _prmDmSensX, _prmDmSensY });

        // Tempo: stored as integer × 100 (e.g. 10000 = 100.0 BPM)
        _tempoLabel.Text = data.Parameters.TryGetValue("TEMPO", out var tempoRaw)
                           && int.TryParse(tempoRaw, out int tempoVal)
            ? $"{tempoVal / 100.0:F1} BPM"
            : "—";

        // Transpose: signed semitones
        _transposeLabel.Text = data.Parameters.TryGetValue("TRANSPOSE", out var trRaw)
                               && int.TryParse(trRaw, out int trVal)
            ? (trVal > 0 ? $"+{trVal}" : trVal.ToString())
            : "0";

        // Motion CC assignments (−1 = unassigned, else a CC number)
        for (int i = 0; i < 8; i++)
        {
            string key = $"MOTION_CC{i + 1}";
            _motionCcLabels[i].Text = data.Parameters.TryGetValue(key, out var mcRaw)
                                      && int.TryParse(mcRaw, out int mcVal) && mcVal >= 0
                ? (_patch.GetByCC(mcVal)?.Name is { } n ? $"CC{mcVal}  {n}" : $"CC {mcVal}")
                : "—";
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

    private IEnumerable<PrmParameter> AllPrmOnlyParams() =>
        _prmDelayMain.Concat(_prmDelayAdv).Concat(new[] { _delayTempo })
                     .Concat(_prmReverbMain).Concat(_prmReverbAdv)
                     .Concat(new[] { _prmChopType, _prmChopCombType,
                                     _prmLeng, _prmShuffle, _prmLevel, _prmScale, _prmTempoSync,
                                     _prmArpType, _prmArpRate,
                                     _prmRiserSw, _prmRiserMode, _prmRiserCtrl, _prmRiserBeat,
                                     _prmRiserShape, _prmRiserReso, _prmRiserLevel,
                                     _prmDmAssignX, _prmDmAssignY, _prmDmAssignTap, _prmDmAssignFf,
                                     _prmDmSensX, _prmDmSensY });

    private void SetStatus(string message, string hexColour)
    {
        StatusText.Text       = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(hexColour));
    }

    // ── Filter modulation: note tracking ─────────────────────────────────────

    private void OnNoteOn()
    {
        _noteCount++;
        _envPhase = EnvPhase.Attack;
        _envLevel = 0;
        if (_patch.GetByCC(105)!.Value == 1)
            _lfoPhase = 0;
    }

    private void OnNoteOff()
    {
        _noteCount = Math.Max(0, _noteCount - 1);
        if (_noteCount == 0)
        {
            _envLevelAtRelease = _envLevel;
            _envPhase = EnvPhase.Release;
        }
    }

    // ── Filter modulation: 60 fps tick ───────────────────────────────────────

    private void OnModTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastModTick).TotalSeconds, 0.1);
        _lastModTick = now;

        // ── Envelope ──────────────────────────────────────────────────────
        double attackSecs  = ModEnvTime(_patch.GetByCC(73)!.Value);
        double decaySecs   = ModEnvTime(_patch.GetByCC(75)!.Value);
        double sustainLvl  = _patch.GetByCC(30)!.Value / 127.0;
        double releaseSecs = ModEnvTime(_patch.GetByCC(72)!.Value);

        switch (_envPhase)
        {
            case EnvPhase.Attack:
                _envLevel = Math.Min(1.0, _envLevel + dt / attackSecs);
                if (_envLevel >= 1.0) _envPhase = EnvPhase.Decay;
                break;
            case EnvPhase.Decay:
                _envLevel = Math.Max(sustainLvl, _envLevel - dt * (1.0 - sustainLvl) / decaySecs);
                if (_envLevel <= sustainLvl) _envPhase = EnvPhase.Sustain;
                break;
            case EnvPhase.Sustain:
                _envLevel = sustainLvl;
                break;
            case EnvPhase.Release:
                _envLevel = Math.Max(0, _envLevel - dt / releaseSecs);
                if (_envLevel <= 0) { _envLevel = 0; _envPhase = EnvPhase.Off; }
                break;
        }

        // ── LFO ───────────────────────────────────────────────────────────
        bool   lfoSync = _patch.GetByCC(106)!.Value == 1;
        bool   lfoFast = _patch.GetByCC(79)!.Value  == 1;
        double lfoHz   = ModLfoHz(_patch.GetByCC(3)!.Value, lfoSync, lfoFast);
        double prevPhase = _lfoPhase;
        _lfoPhase = (_lfoPhase + lfoHz * dt) % 1.0;
        if (_lfoPhase < prevPhase)
            _lfoRandom = Random.Shared.NextDouble() * 2.0 - 1.0;

        double lfoVal = ModLfoValue(_patch.GetByCC(12)!.Value, _lfoPhase, _lfoRandom);

        // ── Combine (only when feature is enabled) ────────────────────────
        if (!_filterModEnabled) return;
        double envAmount = _patch.GetByCC(24)!.Value / 127.0;
        double lfoAmount = _patch.GetByCC(25)!.Value / 127.0;
        _filterModOffset = envAmount * _envLevel + lfoAmount * lfoVal;

        _filterCurveUpdate?.Invoke();
        _envelopeDotUpdate?.Invoke();
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
        0 => phase * 2.0 - 1.0,                                          // Sawtooth
        1 => 1.0 - phase * 2.0,                                          // Inv Saw
        2 => phase < 0.5 ? phase * 4.0 - 1.0 : 3.0 - phase * 4.0,       // Triangle
        3 => phase < 0.5 ? 1.0 : -1.0,                                   // Square
        4 => random,                                                       // S&H (Random)
        _ => Random.Shared.NextDouble() * 2.0 - 1.0,                     // Noise
    };

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _modTimer.Stop();
        _activeInput?.StopEventsListening();
        foreach (var d in _inputDevices) d.Dispose();
        _patch.Dispose();
        base.OnClosed(e);
    }
}
