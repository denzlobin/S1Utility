using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
using S1Utility.Controls;

namespace S1Utility;

public partial class MainWindow : Window
{
    private readonly S1Patch      _patch = new();
    private readonly MidiManager  _midiMgr;
    private readonly PrmFileManager _prm;

    private int MidiChannel => ChannelCombo.SelectedIndex >= 0 ? ChannelCombo.SelectedIndex + 1 : 3;
    private int PcChannel   => ProgramChangeChannelCombo.SelectedIndex >= 0 ? ProgramChangeChannelCombo.SelectedIndex + 1 : 16;

    // Patch/pattern bank buttons (4 groups × 16 patterns = 64 program changes).
    private readonly List<Button> _patchButtons = new();

    // Dirty patch tracking — only active when Patch Mirror is on.
    private readonly Dictionary<int, int[]> _slotSnapshots      = new(); // clean PRM baseline per slot
    private readonly Dictionary<int, int[]> _dirtyStateSnapshots = new(); // user-modified state per slot
    private readonly HashSet<int>           _dirtySlots          = new(); // slots modified since load
    private int    _currentSlotIndex      = -1;
    private bool   _suppressDirtyTracking;
    private Button? _restorePatchButton;

    private bool   _autoConnect;
    private bool   _isConnected;
    private bool   _patternSyncDialogOpen;
    private Window? _seqWindow;
    private int    _midiChannel  = 3;
    private int    _pcChannel    = 3;
    private Action<bool>? _setPatchMirrorEnabled;
    private Action<bool>? _setPatchMirrorActive;
    private Action<bool>? _setLiveViewEnabled;
    private Action<bool>? _setLiveViewActive;
    private static readonly string SettingsPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "s1editor.settings.json");

    // ── Filter modulation animation ───────────────────────────────────────────

    private readonly S1EditorViewModel _viewModel;

    private Action?  _filterCurveUpdate;
    private Action?  _envelopeDotUpdate;
    private DateTime _lastModTick;
    private DateTime _lastMidiActivity = DateTime.MinValue;
    private bool     _midiDotLit;
    private static readonly IBrush MidiDotActive = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush MidiDotIdle   = new SolidColorBrush(Color.Parse("#2C2C3A"));
    private readonly DispatcherTimer _modTimer = new();

    // ── Aspect-ratio scaling ──────────────────────────────────────────────────
    private const  double DesignWidth  = 1100;
    private const  double DesignHeight = 1100;
    private const  double AspectRatio  = DesignWidth / DesignHeight;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _arWndProcDelegate;
    private IntPtr           _arOldWndProc;

    private static readonly string[] s_dmAssignNames =
        { "Off", "Modulation", "Frequency", "Resonance", "Pitch Bend", "Pan", "Expression", "Delay Level", "Reverb Level" };

    // Brushes reused across all 64 step buttons.
    private static readonly IBrush s_chopOnBrush   = new SolidColorBrush(Color.Parse("#CC2222"));
    private static readonly IBrush s_chopOffBrush  = new SolidColorBrush(Color.Parse("#252530"));
    private static readonly IBrush s_chopBorder    = new SolidColorBrush(Color.Parse("#505050"));
    private static readonly IBrush s_chopOnBorder  = new SolidColorBrush(Color.Parse("#E03030"));
    private static readonly IBrush s_chopOffBorder = new SolidColorBrush(Color.Parse("#1A1A24"));


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
    private static readonly IBrush VoiceAccent = new SolidColorBrush(Color.Parse("#C05878"));
    private static readonly IBrush FxAccent    = new SolidColorBrush(Color.Parse("#40C8A8"));
    private static readonly IBrush DmAccent    = new SolidColorBrush(Color.Parse("#60A8E0"));
    private static readonly IBrush WarnBrush   = new SolidColorBrush(Color.Parse("#D0702A"));

    public MainWindow()
    {
        _viewModel = new S1EditorViewModel(_patch);
        _prm       = new PrmFileManager(_patch);
        _midiMgr   = new MidiManager(_patch);

        _prm.MetaLoaded    += OnPrmMetaLoaded;
        _prm.StatusChanged += (_, args) => SetStatus(args.Message, args.Color);

        _midiMgr.Disconnected          += (_, _) => Dispatcher.UIThread.Post(OnDeviceDisconnected);
        _midiMgr.NoteOnReceived        += (_, _) => Dispatcher.UIThread.Post(_viewModel.NoteOn);
        _midiMgr.NoteOffReceived       += (_, _) => Dispatcher.UIThread.Post(_viewModel.NoteOff);
        _midiMgr.ProgramChangeReceived += (_, prog) => Dispatcher.UIThread.Post(() => HighlightPatchButton(prog));
        _midiMgr.ActivityReceived      += (_, _) => _lastMidiActivity = DateTime.UtcNow;

        _patch.SyncCountChanged += (_, count) => Dispatcher.UIThread.Post(() => UpdateSyncIndicator(count));

        for (int i = 0; i < 8; i++)
            _motionCcLabels[i] = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };

        InitializeComponent();

        Opened += (_, _) =>
        {
            FitToScreen();
            if (OperatingSystem.IsWindows())
                HookAspectRatio();
        };

        LoadSettings();
        PopulateDeviceLists();
        BuildRealtimeEditorPanels();
        BuildPrmViewerContent();
        _prm.ApplyInitPatch();

        ConnectButton.Click          += OnConnectClicked;
        RefreshDevicesButton.Click   += (_, _) => RefreshDevices();
        SendAllButton.Click          += OnSendAllClicked;
        SaveButton.Click             += OnSaveClicked;
        LoadButton.Click             += OnLoadClicked;
        OpenPrmButton.Click          += OnOpenPrmClicked;
        PrmInfoToggle.Click          += (_, _) => PrmInfoText.IsVisible = !PrmInfoText.IsVisible;
        BrowsePrmFolderButton.Click  += OnBrowsePrmFolderClicked;

        PrmFolderBox.Text = _prm.PrmFolder;
        BuildLiveFeaturesPanel();
        UpdateSyncIndicator(_patch.UnsyncedCount);
        if (_autoConnect) TryAutoConnect();

        // Subscribe after ApplyInitPatch so the init run does not trigger dirty marks.
        foreach (var param in _patch.AllParameters)
            param.ValueChanged += (_, _) => MarkCurrentSlotDirty();

        _lastModTick = DateTime.UtcNow;
        _modTimer.Interval = TimeSpan.FromMilliseconds(16);
        _modTimer.Tick += OnModTimerTick;
        _modTimer.Start();
    }

    // ── Device lists ──────────────────────────────────────────────────────────

    private void PopulateDeviceLists()
    {
        foreach (var port in _midiMgr.OutputPorts)
            DeviceCombo.Items.Add(port.Name);

        foreach (var name in _midiMgr.InputDeviceNames)
            InputCombo.Items.Add(name);

        for (int ch = 1; ch <= 16; ch++)
        {
            ChannelCombo.Items.Add(ch.ToString());
            ProgramChangeChannelCombo.Items.Add(ch.ToString());
        }
        ChannelCombo.SelectedIndex              = _midiChannel - 1;
        ProgramChangeChannelCombo.SelectedIndex = _pcChannel   - 1;

        ChannelCombo.SelectionChanged += (_, _) => SaveSettings();
        ProgramChangeChannelCombo.SelectionChanged += (_, _) => SaveSettings();

        if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
        if (InputCombo.Items.Count  > 0) InputCombo.SelectedIndex  = 0;
    }

    private void RefreshDevices()
    {
        string? prevOut = DeviceCombo.SelectedIndex >= 0
            ? DeviceCombo.Items[DeviceCombo.SelectedIndex] as string : null;
        string? prevIn = _midiMgr.InputDeviceNames.Count > 0
            ? (InputCombo.SelectedIndex >= 0
                ? _midiMgr.InputDeviceNames[InputCombo.SelectedIndex] : null)
            : null;

        _midiMgr.EnumerateDevices();

        DeviceCombo.Items.Clear();
        foreach (var port in _midiMgr.OutputPorts)
            DeviceCombo.Items.Add(port.Name);

        InputCombo.Items.Clear();
        foreach (var name in _midiMgr.InputDeviceNames)
            InputCombo.Items.Add(name);

        // Restore previous selections by name
        if (prevOut != null)
        {
            int idx = DeviceCombo.Items.Cast<string>().ToList().IndexOf(prevOut);
            DeviceCombo.SelectedIndex = idx >= 0 ? idx : (DeviceCombo.Items.Count > 0 ? 0 : -1);
        }
        else if (DeviceCombo.Items.Count > 0)
            DeviceCombo.SelectedIndex = 0;

        if (prevIn != null)
        {
            int idx = _midiMgr.FindInputIndex(n => n == prevIn);
            if (idx >= 0) InputCombo.SelectedIndex = idx;
        }
        else if (InputCombo.Items.Count > 0)
            InputCombo.SelectedIndex = 0;

        if (_autoConnect)
            TryAutoConnect();
        else
            SetStatus("Devices refreshed.", "#A0A0B8");
    }

    // ── Tab 1: Realtime editor ────────────────────────────────────────────────

    // Throws if a required CC is missing — gives a clear error instead of a NullReferenceException.
    private S1Parameter RequireCC(int cc) =>
        _patch.GetByCC(cc) ?? throw new InvalidOperationException($"Required parameter CC {cc} not found in patch");

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
            knobRow1.Children.Add(MakeKnob(RequireCC(cc), OscAccent));
        OscillatorPanel.Children.Add(knobRow1);

        // Row 2: modulation / tuning knobs
        var knobRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 15, 13, 76, 18 })
            knobRow2.Children.Add(MakeKnob(RequireCC(cc), OscAccent));
        OscillatorPanel.Children.Add(knobRow2);

        // Button strips stacked vertically — all full-width, uniform button cells per strip
        OscillatorPanel.Children.Add(MakeLedButtonGroup(RequireCC(14), OscAccent));  // Range
        OscillatorPanel.Children.Add(MakeLedButtonGroup(RequireCC(78), OscAccent));  // Noise Mode
        OscillatorPanel.Children.Add(MakeLedButtonGroup(RequireCC(16), OscAccent));  // PWM Source
        OscillatorPanel.Children.Add(MakeLedButtonGroup(RequireCC(22), OscAccent));  // Sub Octave

        OscillatorPanel.Children.Add(MakeSubSectionHeader("DRAW · CHOP", OscAccent));

        var dcKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        dcKnobs.Children.Add(MakeKnob(RequireCC(102), OscAccent, minCcValue: 3));
        dcKnobs.Children.Add(MakeKnob(RequireCC(104), OscAccent, minCcValue: 3));
        dcKnobs.Children.Add(MakeKnob(RequireCC(103), OscAccent));
        OscillatorPanel.Children.Add(dcKnobs);

        OscillatorPanel.Children.Add(MakeLedButtonGroup(RequireCC(107), OscAccent));
    }

    private void BuildFilterPanel()
    {
        FilterPanel.Children.Add(MakeFilterCurve(RequireCC(74), RequireCC(71)));

        var filtRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 74, 71, 24 })
            filtRow1.Children.Add(MakeKnob(RequireCC(cc), FiltAccent));
        FilterPanel.Children.Add(filtRow1);

        var filtRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 25, 26, 27 })
            filtRow2.Children.Add(MakeKnob(RequireCC(cc), FiltAccent));
        FilterPanel.Children.Add(filtRow2);
    }

    private void BuildEnvelopePanel()
    {
        EnvelopePanel.Children.Add(MakeAdsrVisualizer(
            RequireCC(73), RequireCC(75), RequireCC(30), RequireCC(72)));

        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 73, 75, 30, 72 })
            knobs.Children.Add(MakeKnob(RequireCC(cc), EnvAccent));
        EnvelopePanel.Children.Add(knobs);

        var btnGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var ampBtn = MakeLedButtonGroup(RequireCC(28), EnvAccent);
        var trgBtn = MakeLedButtonGroup(RequireCC(29), EnvAccent);
        Grid.SetColumn(ampBtn, 0); Grid.SetColumn(trgBtn, 1);
        btnGrid.Children.Add(ampBtn); btnGrid.Children.Add(trgBtn);
        EnvelopePanel.Children.Add(btnGrid);
    }

    private void BuildLfoPanel()
    {
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        knobs.Children.Add(MakeLfoRateKnob());
        knobs.Children.Add(MakeKnob(RequireCC(17), LfoAccent));
        LfoPanel.Children.Add(knobs);

        LfoPanel.Children.Add(MakeLedButtonGroup(RequireCC(12), LfoAccent));

        var modeRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 2,
            Margin              = new Thickness(3, 1, 3, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        modeRow.Children.Add(MakeLedButtonGroup(RequireCC(79),  LfoAccent));
        modeRow.Children.Add(MakeLedButtonGroup(RequireCC(106), LfoAccent));
        modeRow.Children.Add(MakeLedButtonGroup(RequireCC(105), LfoAccent));
        LfoPanel.Children.Add(modeRow);
    }

    private void BuildVoicePanel()
    {
        // 5 knobs × 56px = 280px + margins ≈ 300px — fits the column without overflow
        var knobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int cc in new[] { 1, 11, 5, 10, 77 })
            knobs.Children.Add(MakeKnob(RequireCC(cc), VoiceAccent, containerWidth: 56));
        VoicePanel.Children.Add(knobs);

        // Portamento and Polyphony stacked vertically — side-by-side caused overflow with 6-option strip
        VoicePanel.Children.Add(MakeLedButtonGroup(RequireCC(31), VoiceAccent));
        VoicePanel.Children.Add(MakeLedButtonGroup(RequireCC(80), VoiceAccent));

        VoicePanel.Children.Add(MakeDroneButton(RequireCC(64), VoiceAccent));

        var chordSection = new StackPanel();
        chordSection.Children.Add(MakeSubSectionHeader("CHORD", VoiceAccent));
        chordSection.Children.Add(MakeChordVoiceRow(2, RequireCC(81), RequireCC(85), VoiceAccent));
        chordSection.Children.Add(MakeChordVoiceRow(3, RequireCC(82), RequireCC(86), VoiceAccent));
        chordSection.Children.Add(MakeChordVoiceRow(4, RequireCC(83), RequireCC(87), VoiceAccent));
        VoicePanel.Children.Add(chordSection);

        var polyParam = RequireCC(80);
        void UpdateChordEnabled(int v)
        {
            bool isChord = v == 3;
            chordSection.IsEnabled = isChord;
            chordSection.Opacity   = isChord ? 1.0 : 0.3;
        }
        UpdateChordEnabled(polyParam.Value);
        polyParam.ValueChanged += (_, v) => Dispatcher.UIThread.Post(() => UpdateChordEnabled(v));

        var portModeParam = RequireCC(31);
        var portOnParam   = RequireCC(65);
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
                    borders[i].Background  = on
                        ? new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))
                        : new SolidColorBrush(Color.Parse("#191919"));
                    borders[i].BorderBrush = on ? accent : new SolidColorBrush(Color.Parse("#303030"));
                    setColor[i](on ? accent : new SolidColorBrush(Color.Parse("#4A4A4A")));
                }
                else
                {
                    borders[i].Background  = new SolidColorBrush(Color.Parse("#191919"));
                    borders[i].BorderBrush = new SolidColorBrush(Color.Parse("#252525"));
                    setColor[i](new SolidColorBrush(Color.Parse("#2A2A2A")));
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
            {
                param.MarkSynced();
                param.Value = param.ParameterType == S1ParameterType.Toggle ? (idx > 0 ? 127 : 0) : idx;
            };
            borders[i] = cell;
            row.Children.Add(cell);
        }

        Refresh(param.Value);
        param.ValueChanged      += (_, v)      => Dispatcher.UIThread.Post(() => Refresh(v));
        param.SyncStateChanged  += (_, synced) => Dispatcher.UIThread.Post(() =>
        {
            isSynced = synced;
            Refresh(param.Value);
        });

        return new StackPanel
        {
            Margin              = new Thickness(3, 2, 3, 4),
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

        var canvas = new Canvas { Width = W, Height = H, Margin = new Thickness(4, 0, 2, 3), ClipToBounds = true };
        canvas.Children.Add(fillPath);
        canvas.Children.Add(linePath);
        canvas.Children.Add(marker);

        void Update()
        {
            const double logMin = 1.30103; // log10(20 Hz)
            const double logMax = 4.30103; // log10(20 kHz)
            const int    N      = 200;
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

            var pts = new Point[N];
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

        void Update()
        {
            var (aT, dT, hw, rT, sL) = ComputeAdsrLayout();

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
            if (!_viewModel.FilterModEnabled || _viewModel.CurrentPhase == EnvPhase.Off)
            {
                dot.IsVisible = false;
                return;
            }

            var (aT2, dT2, hw2, rT2, sL2) = ComputeAdsrLayout();
            double lvl  = _viewModel.EnvLevel;

            double dx, dy;
            switch (_viewModel.CurrentPhase)
            {
                case EnvPhase.Attack:
                    dx = lvl * aT2;
                    dy = H - lvl * (H - 3);
                    break;
                case EnvPhase.Decay:
                    double sN = sustainP.Value / 127.0;
                    double dd = 1.0 - sN > 0.001
                        ? Math.Clamp((1.0 - lvl) / (1.0 - sN), 0, 1) : 1.0;
                    dx = aT2 + dd * dT2;
                    dy = 3 + dd * (sL2 - 3);
                    break;
                case EnvPhase.Sustain:
                    dx = aT2 + dT2;
                    dy = sL2;
                    break;
                case EnvPhase.Release:
                    double rp = _viewModel.EnvLevelAtRelease > 0.001
                        ? Math.Clamp(1.0 - lvl / _viewModel.EnvLevelAtRelease, 0, 1) : 1.0;
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

        bool toggleIsSynced = toggleParam.IsSynced;

        void RefreshToggle()
        {
            bool on = toggleParam.Value > 0;
            if (toggleIsSynced)
            {
                toggle.Background    = on ? new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) : new SolidColorBrush(Color.Parse("#191919"));
                toggle.BorderBrush   = on ? accent : new SolidColorBrush(Color.Parse("#2E2E2E"));
                toggleLbl.Foreground = on ? accent : new SolidColorBrush(Color.Parse("#444444"));
            }
            else
            {
                toggle.Background    = new SolidColorBrush(Color.Parse("#191919"));
                toggle.BorderBrush   = new SolidColorBrush(Color.Parse("#252525"));
                toggleLbl.Foreground = new SolidColorBrush(Color.Parse("#333333"));
            }
        }
        RefreshToggle();
        toggle.PointerPressed += (_, _) =>
        {
            toggleParam.MarkSynced();
            toggleParam.Value = toggleParam.Value > 0 ? 0 : 127;
        };
        toggleParam.ValueChanged  += (_, _) => Dispatcher.UIThread.Post(RefreshToggle);
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

        bool droneIsSynced = param.IsSynced;

        void Refresh()
        {
            bool on  = param.Value > 0;
            var  col = ((SolidColorBrush)accent).Color;
            if (droneIsSynced)
            {
                btn.Background   = on ? new SolidColorBrush(Color.FromArgb(0x14, col.R, col.G, col.B))
                                      : new SolidColorBrush(Color.Parse("#161616"));
                btn.BorderBrush  = on ? accent : new SolidColorBrush(Color.Parse("#2E2E2E"));
                led.Background   = on ? accent : new SolidColorBrush(Color.Parse("#2A2A2A"));
                label.Foreground = on ? accent : new SolidColorBrush(Color.Parse("#555555"));
            }
            else
            {
                btn.Background   = new SolidColorBrush(Color.Parse("#161616"));
                btn.BorderBrush  = new SolidColorBrush(Color.Parse("#252525"));
                led.Background   = new SolidColorBrush(Color.Parse("#2A2A2A"));
                label.Foreground = new SolidColorBrush(Color.Parse("#404040"));
            }
        }
        Refresh();
        btn.PointerPressed += (_, _) =>
        {
            param.MarkSynced();
            param.Value = param.Value > 0 ? 0 : 127;
        };
        param.ValueChanged       += (_, _) => Dispatcher.UIThread.Post(Refresh);
        param.SyncStateChanged   += (_, synced) => Dispatcher.UIThread.Post(() =>
        {
            droneIsSynced = synced;
            Refresh();
        });

        return btn;
    }

    // ── Live heuristic feature toggles ───────────────────────────────────────────

    // Returns the toggle button plus two delegates to update its visual state
    // from outside: setActive(bool) and setEnabled(bool).
    private (Border btn, Action<bool> setActive, Action<bool> setEnabled) MakeHeuristicToggle(
        string name, string tooltip, IBrush accent)
    {
        var led = new Border
        {
            Width             = 6,
            Height            = 6,
            CornerRadius      = new CornerRadius(3),
            Margin            = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameLbl = new TextBlock
        {
            Text              = name,
            FontSize          = 9.5,
            FontWeight        = FontWeight.SemiBold,
            LetterSpacing     = 0.5,
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(4),
            Padding             = new Thickness(9, 8),
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = nameRow,
        };
        ToolTip.SetTip(btn, tooltip);

        void SetActive(bool on)
        {
            var col = ((SolidColorBrush)accent).Color;
            btn.Background     = on ? new SolidColorBrush(Color.FromArgb(0x18, col.R, col.G, col.B))
                                    : new SolidColorBrush(Color.Parse("#161616"));
            btn.BorderBrush    = on ? accent : new SolidColorBrush(Color.Parse("#2E2E2E"));
            led.Background     = on ? accent : new SolidColorBrush(Color.Parse("#2A2A2A"));
            nameLbl.Foreground = on ? accent : new SolidColorBrush(Color.Parse("#555555"));
        }

        void SetEnabled(bool enabled)
        {
            btn.IsEnabled = enabled;
            btn.Opacity   = enabled ? 1.0 : 0.4;
        }

        SetActive(false);
        return (btn, SetActive, SetEnabled);
    }

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

        _setPatchMirrorEnabled?.Invoke(valid);
        _setPatchMirrorActive?.Invoke(patchMirrorOn);
        _setLiveViewEnabled?.Invoke(patchMirrorOn);

        if (!patchMirrorOn && _viewModel.FilterModEnabled)
        {
            _viewModel.FilterModEnabled = false;
            _setLiveViewActive?.Invoke(false);
            _filterCurveUpdate?.Invoke();
            _envelopeDotUpdate?.Invoke();
        }
    }

    private void BuildLiveFeaturesPanel()
    {
        // ── Auto-connect ──────────────────────────────────────────────────────
        var (autoBtn, setAutoActive, _) = MakeHeuristicToggle(
            "AUTO-CONNECT",
            "Heuristic: searches MIDI device names for \"S-1\" and connects automatically on launch " +
            "and after a device refresh. Name matching only; any device containing \"S-1\" will match " +
            "regardless of model. Verify the right device is selected after auto-connect.",
            DmAccent);

        setAutoActive(_autoConnect);
        autoBtn.PointerPressed += (_, _) =>
        {
            _autoConnect = !_autoConnect;
            setAutoActive(_autoConnect);
            SaveSettings();
        };

        // ── Live View: filter curve + ADSR animation (requires Patch Mirror) ──
        var (liveViewBtn, setLiveViewActive, setLiveViewEnabled) = MakeHeuristicToggle(
            "ANIMATIONS",
            "Heuristic: animates the filter curve and ADSR dot at 60 fps using the editor's current " +
            "CC values as model inputs. The envelope and LFO routing is an approximation; it responds " +
            "to note events but will not match the S-1 hardware signal path exactly.\n\n" +
            "Requires Patch Mirror so the editor values reflect what is on the device.",
            EnvAccent);

        _setLiveViewActive  = setLiveViewActive;
        _setLiveViewEnabled = setLiveViewEnabled;

        bool prmValid      = PrmFolderHasValidFiles();
        bool patchMirrorOn = prmValid && _prm.PatternSync;

        if (!patchMirrorOn && _viewModel.FilterModEnabled)
            _viewModel.FilterModEnabled = false;

        setLiveViewActive(_viewModel.FilterModEnabled);
        setLiveViewEnabled(patchMirrorOn);

        liveViewBtn.PointerPressed += (_, _) =>
        {
            _viewModel.FilterModEnabled = !_viewModel.FilterModEnabled;
            setLiveViewActive(_viewModel.FilterModEnabled);
            if (!_viewModel.FilterModEnabled)
            {
                _filterCurveUpdate?.Invoke();
                _envelopeDotUpdate?.Invoke();
            }
            SaveSettings();
        };

        // ── Patch Mirror ──────────────────────────────────────────────────────
        var (patchMirrorBtn, setPatchMirrorActive, setPatchMirrorEnabled) = MakeHeuristicToggle(
            "PATCH MIRROR",
            "Heuristic: auto-loads the PRM file matching the current pattern number when you switch " +
            "patterns (via the editor or a MIDI Program Change). The editor is updated only; no values " +
            "are sent back to the S-1.\n\n" +
            "Requires a PRM folder containing valid .PRM files. Accuracy depends on keeping the " +
            "folder in sync with what is stored on the device.",
            WarnBrush);

        _setPatchMirrorEnabled = setPatchMirrorEnabled;
        _setPatchMirrorActive  = setPatchMirrorActive;

        setPatchMirrorEnabled(prmValid);
        setPatchMirrorActive(patchMirrorOn);

        patchMirrorBtn.PointerPressed += async (_, _) =>
        {
            if (_patternSyncDialogOpen) return;
            bool enabling = !_prm.PatternSync;
            if (enabling)
            {
                _patternSyncDialogOpen = true;
                bool confirmed = await ShowPatternSyncWarningAsync();
                _patternSyncDialogOpen = false;
                if (!confirmed) return;
            }
            _prm.PatternSync = enabling;
            setPatchMirrorActive(enabling);
            setLiveViewEnabled(enabling);
            if (enabling)
            {
                if (_isConnected)
                    GoToPattern1();
                UpdateRestorePatchButton();
            }
            else
            {
                _viewModel.FilterModEnabled = false;
                setLiveViewActive(false);
                _filterCurveUpdate?.Invoke();
                _envelopeDotUpdate?.Invoke();
                ClearDirtyTracking(); // also hides Restore button
            }
            SaveSettings();
        };

        // ── Assemble panel ────────────────────────────────────────────────────
        LiveFeaturesPanel.Children.Add(new TextBlock
        {
            Text          = "LIVE FEATURES",
            FontSize      = 8,
            FontWeight    = FontWeight.Bold,
            LetterSpacing = 1.5,
            Foreground    = new SolidColorBrush(Color.Parse("#44445A")),
            Margin        = new Thickness(0, 0, 0, 3),
        });
        var btnGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions   = new ColumnDefinitions("*,*,*"),
            ColumnSpacing       = 4,
        };
        Grid.SetColumn(autoBtn,        0);
        Grid.SetColumn(patchMirrorBtn, 1);
        Grid.SetColumn(liveViewBtn,    2);
        btnGrid.Children.Add(autoBtn);
        btnGrid.Children.Add(patchMirrorBtn);
        btnGrid.Children.Add(liveViewBtn);
        LiveFeaturesPanel.Children.Add(btnGrid);
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
                Text              = $"BANK {g + 1}",
                FontSize          = 9,
                Foreground        = new SolidColorBrush(Color.Parse("#505050")),
                Width             = 46,
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

        _restorePatchButton = new Button
        {
            Content          = "Restore Patch",
            FontSize         = 9,
            Height           = 18,
            Margin           = new Thickness(0, 4, 0, 0),
            Padding          = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background       = new SolidColorBrush(Color.Parse("#281800")),
            Foreground       = new SolidColorBrush(Color.Parse("#B07828")),
            BorderBrush      = new SolidColorBrush(Color.Parse("#6A4A18")),
            CornerRadius     = new CornerRadius(2),
            IsEnabled        = false,
            IsVisible        = false,
        };
        _restorePatchButton.Click += (_, _) => OnRestorePatchClicked();
        PatchGridContainer.Children.Add(_restorePatchButton);
    }

    private void OnPatchClicked(int program, Button btn)
    {
        _patch.SendProgramChange(program, PcChannel);
        HighlightPatchButton(program);
    }

    private void HighlightPatchButton(int program)
    {
        if (program == _currentSlotIndex) return;

        // Capture the outgoing slot's current state before leaving it.
        if (_currentSlotIndex >= 0 && _dirtySlots.Contains(_currentSlotIndex))
            _dirtyStateSnapshots[_currentSlotIndex] = CaptureSnapshot();

        foreach (var b in _patchButtons)
            b.Classes.Remove("patch-btn-active");
        if ((uint)program < (uint)_patchButtons.Count)
            _patchButtons[program].Classes.Add("patch-btn-active");
        _currentSlotIndex = program;

        if (_prm.PatternSync && !string.IsNullOrEmpty(_prm.PrmFolder))
        {
            if (_dirtySlots.Contains(program) && _dirtyStateSnapshots.TryGetValue(program, out var dirtySnap))
            {
                // Restore the user's modified values, not the clean PRM baseline.
                RestoreSnapshotValues(dirtySnap);
                _patch.MarkAllSynced();
            }
            else
            {
                _suppressDirtyTracking = true;
                bool loaded = _prm.TryLoadPatternPrm(program);
                _suppressDirtyTracking = false;
                if (loaded) _slotSnapshots[program] = CaptureSnapshot();
            }
            UpdateRestorePatchButton();
        }
        else
        {
            ClearDirtyTracking();
            _patch.ResetAllSync();
        }
    }

    private int[] CaptureSnapshot() =>
        _patch.AllParameters.Select(p => p.Value).ToArray();

    private void RestoreSnapshotValues(int[] snapshot)
    {
        _suppressDirtyTracking = true;
        for (int i = 0; i < _patch.AllParameters.Count; i++)
            _patch.HandleIncomingCC(_patch.AllParameters[i].CcNumber, snapshot[i]);
        _suppressDirtyTracking = false;
    }

    private void MarkCurrentSlotDirty()
    {
        if (_suppressDirtyTracking || !_prm.PatternSync || _currentSlotIndex < 0) return;
        if (_dirtySlots.Add(_currentSlotIndex))
        {
            RefreshPatchButtonStyle(_currentSlotIndex);
            UpdateRestorePatchButton();
        }
    }

    private void RefreshPatchButtonStyle(int slot)
    {
        if ((uint)slot >= (uint)_patchButtons.Count) return;
        var btn = _patchButtons[slot];
        if (_prm.PatternSync && _dirtySlots.Contains(slot))
            btn.Classes.Add("patch-btn-dirty");
        else
            btn.Classes.Remove("patch-btn-dirty");
    }

    private void RefreshAllPatchButtonStyles()
    {
        for (int i = 0; i < _patchButtons.Count; i++)
            RefreshPatchButtonStyle(i);
    }

    private void UpdateRestorePatchButton()
    {
        if (_restorePatchButton == null) return;
        bool mirrorOn = _prm.PatternSync;
        _restorePatchButton.IsVisible = mirrorOn;
        _restorePatchButton.IsEnabled = mirrorOn
            && _currentSlotIndex >= 0
            && _dirtySlots.Contains(_currentSlotIndex);
    }

    private void ClearDirtyTracking()
    {
        _dirtySlots.Clear();
        _slotSnapshots.Clear();
        _dirtyStateSnapshots.Clear();
        RefreshAllPatchButtonStyles();
        UpdateRestorePatchButton();
    }

    private async void OnRestorePatchClicked()
    {
        if (_currentSlotIndex < 0 || !_dirtySlots.Contains(_currentSlotIndex)) return;

        _suppressDirtyTracking = true;
        bool loaded = _prm.TryLoadPatternPrm(_currentSlotIndex);
        _suppressDirtyTracking = false;
        if (!loaded) return;

        _dirtySlots.Remove(_currentSlotIndex);
        _dirtyStateSnapshots.Remove(_currentSlotIndex);
        _slotSnapshots[_currentSlotIndex] = CaptureSnapshot();
        RefreshPatchButtonStyle(_currentSlotIndex);
        UpdateRestorePatchButton();
        await _patch.SendAllAsync();
        _patch.MarkAllSynced();
    }

    private static string GetKnobDisplayValue(S1Parameter param)
    {
        int cc  = param.CcNumber;
        int val = param.Value;

        return cc switch
        {
            76  => (val - 64).ToString(),
            77  => FormatSemitone(val - 64),
            102 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}", // CC 3–127 → display 1.0–32.0 in 0.5 steps
            103 => Math.Min(200, (int)Math.Round(val * 255.0 / 127)).ToString(),
            104 => $"{Math.Round((1.0 + (val - 3) * 31.0 / 124.0) * 2) / 2.0:F1}", // same scale as CC 102
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
            "DM_ASSIGN_X" or "DM_ASSIGN_Y"
                or "DM_ASSIGN_TAP" or "DM_ASSIGN_FF"
                                            => v < s_dmAssignNames.Length ? s_dmAssignNames[v] : v.ToString(),
            _                               => v.ToString(),
        };
    }

    private static Control MakeKnob(S1Parameter param, IBrush accent, int minCcValue = 0, double containerWidth = 68)
    {
        string initDisplay = GetKnobDisplayValue(param);

        var knob = new RotaryKnob { Value = param.Value, AccentBrush = accent, IsSynced = param.IsSynced, MinValue = minCcValue };
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
            param.MarkSynced();
            string display = GetKnobDisplayValue(param);
            ToolTip.SetTip(knob, $"{param.Name}: {display}");
            valueLabel.Text = display;
        };

        param.ValueChanged += (_, v) =>
            Dispatcher.UIThread.Post(() =>
            {
                updatingFromModel = true;
                knob.Value = v;
                updatingFromModel = false;
                string display = GetKnobDisplayValue(param);
                ToolTip.SetTip(knob, $"{param.Name}: {display}");
                valueLabel.Text = param.IsSynced ? display : "?";
            });

        param.SyncStateChanged += (_, synced) =>
            Dispatcher.UIThread.Post(() =>
            {
                knob.IsSynced = synced;
                string display = GetKnobDisplayValue(param);
                valueLabel.Text = synced ? display : "?";
            });

        var nameLabel = new TextBlock { Classes = { "param-label" }, Text = param.Name };
        bool small = containerWidth <= 58;
        var container = new Grid
        {
            Width          = containerWidth,
            Margin         = new Thickness(small ? 2 : 3, 4),
            RowDefinitions = small
                ? new RowDefinitions("56,14,20")
                : new RowDefinitions("56,14,26"),
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
        revKnobs.Children.Add(MakeKnob(RequireCC(91), FxAccent));
        revKnobs.Children.Add(MakeKnob(RequireCC(89), FxAccent));
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
        delKnobs.Children.Add(MakeKnob(RequireCC(92), FxAccent));
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
        EffectsPanel.Children.Add(MakeLedButtonGroup(RequireCC(93), FxAccent));
    }

    private Control BuildDelayTimeKnob()
    {
        var param   = RequireCC(90);
        var delaySw = _prm.DelayMain[0];

        string GetDisplay()
        {
            if (delaySw.Value != 1)
                return $"{1 + (int)Math.Round(param.Value * 739.0 / 127)}ms"; // S-1 range: 1–740 ms
            var opts = _prm.DelayTempo.Options!;
            int idx  = Math.Clamp(param.Value, 0, opts.Length - 1);  // CC 0-15 → index 0-15
            return opts[idx];
        }

        var knob       = new RotaryKnob { Value = param.Value, AccentBrush = FxAccent, IsSynced = param.IsSynced };
        var valueLabel = new TextBlock   { Classes = { "param-value-label" }, Text = param.IsSynced ? GetDisplay() : "?" };
        ToolTip.SetTip(knob, $"{param.Name}: {GetDisplay()}");

        void Refresh()
        {
            string d = GetDisplay();
            ToolTip.SetTip(knob, $"{param.Name}: {d}");
            valueLabel.Text = param.IsSynced ? d : "?";
        }

        // When sync is on, CC 0–N maps evenly across the full knob rotation.
        int delaySteps = _prm.DelayTempo.Options!.Length - 1;
        int SyncToKnob(int cc)   => (int)Math.Round(Math.Clamp(cc, 0, delaySteps) * 127.0 / delaySteps);
        int KnobToSync(int knob) => Math.Clamp((int)Math.Round(knob * (double)delaySteps / 127), 0, delaySteps);

        bool delayUpdatingFromModel = false;

        knob.ValueChanged    += (_, v) =>
        {
            if (delayUpdatingFromModel) return;
            param.Value = delaySw.Value == 1 ? KnobToSync(v) : v;
            param.MarkSynced();
            Refresh();
        };
        param.ValueChanged   += (_, v) => Dispatcher.UIThread.Post(() =>
        {
            delayUpdatingFromModel = true;
            knob.Value = delaySw.Value == 1 ? SyncToKnob(v) : v;
            delayUpdatingFromModel = false;
            Refresh();
        });
        delaySw.ValueChanged += (_, sw) => Dispatcher.UIThread.Post(() =>
        {
            delayUpdatingFromModel = true;
            knob.Value = sw == 1 ? SyncToKnob(param.Value) : param.Value;
            delayUpdatingFromModel = false;
            Refresh();
        });
        param.SyncStateChanged += (_, synced) => Dispatcher.UIThread.Post(() => { knob.IsSynced = synced; Refresh(); });

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
        var param  = RequireCC(3);
        var syncSw = RequireCC(106);

        string GetDisplay()
        {
            if (syncSw.Value == 0) return ((int)Math.Round(param.Value * 255.0 / 127)).ToString();
            int idx = Math.Clamp(param.Value, 0, s_lfoSyncValues.Length - 1);
            return s_lfoSyncValues[idx];
        }

        var knob       = new RotaryKnob { Value = param.Value, AccentBrush = LfoAccent, IsSynced = param.IsSynced };
        var valueLabel = new TextBlock   { Classes = { "param-value-label" }, Text = param.IsSynced ? GetDisplay() : "?" };
        ToolTip.SetTip(knob, $"{param.Name}: {GetDisplay()}");

        void Refresh()
        {
            string d = GetDisplay();
            ToolTip.SetTip(knob, $"{param.Name}: {d}");
            valueLabel.Text = param.IsSynced ? d : "?";
        }

        // When sync on, CC 0–N spread evenly across full knob rotation.
        int lfoSteps = s_lfoSyncValues.Length - 1;
        int SyncToKnob(int cc)   => (int)Math.Round(Math.Clamp(cc, 0, lfoSteps) * 127.0 / lfoSteps);
        int KnobToSync(int knob) => Math.Clamp((int)Math.Round(knob * (double)lfoSteps / 127), 0, lfoSteps);

        bool lfoUpdatingFromModel = false;

        knob.ValueChanged   += (_, v) =>
        {
            if (lfoUpdatingFromModel) return;
            param.Value = syncSw.Value == 1 ? KnobToSync(v) : v;
            param.MarkSynced();
            Refresh();
        };
        param.ValueChanged  += (_, v) => Dispatcher.UIThread.Post(() =>
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
        param.SyncStateChanged += (_, synced) => Dispatcher.UIThread.Post(() => { knob.IsSynced = synced; Refresh(); });

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
        oscContent.Children.Add(MakeTwoColumnGrid(
            mainOscForViewer.Select(p => (Control)MakePrmViewerCcRowCompact(p)).ToList()));

        oscContent.Children.Add(MakeSubSectionHeader("OSC DRAW", OscAccent));
        oscContent.Children.Add(MakePrmViewerCcRow(RequireCC(102)));  // Draw Multiply
        oscContent.Children.Add(MakePrmViewerCcRow(RequireCC(107)));  // Draw Step/Slope
        oscContent.Children.Add(new Viewbox
        {
            Stretch   = Stretch.Uniform,
            MaxHeight = 60,
            Margin    = new Thickness(0, 4, 0, 4),
            Child     = MakeDrawBarsControl(),
        });

        oscContent.Children.Add(MakeSubSectionHeader("OSC CHOP", OscAccent));
        oscContent.Children.Add(MakePrmViewerCcRow(RequireCC(103)));  // Chop Overtone
        oscContent.Children.Add(MakePrmViewerCcRow(RequireCC(104)));  // Chop Comb
        oscContent.Children.Add(MakePrmInfoRow(_prm.ChopType));
        oscContent.Children.Add(MakePrmInfoRow(_prm.ChopCombType));
        oscContent.Children.Add(MakeChopPatternControl());

        var riserCard = MakeSectionCard("RISER", FxAccent, out var riserContent);
        riserContent.Children.Add(MakeTwoColumnGrid(new List<Control>
        {
            MakePrmInfoRow(_prm.RiserSw,    compact: true),
            MakePrmInfoRow(_prm.RiserMode,  compact: true),
            MakePrmInfoRow(_prm.RiserCtrl,  compact: true),
            MakePrmInfoRow(_prm.RiserBeat,  compact: true),
            MakePrmInfoRow(_prm.RiserShape, compact: true),
            MakePrmInfoRow(_prm.RiserReso,  compact: true),
            MakePrmInfoRow(_prm.RiserLevel, compact: true),
        }));

        Grid.SetColumn(oscCard, 0); Grid.SetRow(oscCard, 0); Grid.SetRowSpan(oscCard, 3);
        PrmViewerGrid.Children.Add(oscCard);

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

        var envCard = MakeSectionCard("ENVELOPE", EnvAccent, out var envContent);
        envContent.Children.Add(MakeTwoColumnGrid(
            _patch.Envelope.Select(p => (Control)MakePrmViewerCcRowCompact(p)).ToList()));

        var lfoCard = MakeSectionCard("LFO", LfoAccent, out var lfoContent);
        lfoContent.Children.Add(MakeTwoColumnGrid(
            _patch.Lfo.Select(p => (Control)MakePrmViewerCcRowCompact(p)).ToList()));
        var col1Row2Grid = new Grid { RowDefinitions = new RowDefinitions("*,*"), RowSpacing = 4 };
        Grid.SetRow(lfoCard,   0);
        Grid.SetRow(riserCard, 1);
        col1Row2Grid.Children.Add(lfoCard);
        col1Row2Grid.Children.Add(riserCard);

        var col1Grid = new Grid { RowDefinitions = new RowDefinitions("*,*,*"), RowSpacing = 4 };
        Grid.SetRow(filterCard,   0);
        Grid.SetRow(envCard,      1);
        Grid.SetRow(col1Row2Grid, 2);
        col1Grid.Children.Add(filterCard);
        col1Grid.Children.Add(envCard);
        col1Grid.Children.Add(col1Row2Grid);
        Grid.SetColumn(col1Grid, 1); Grid.SetRow(col1Grid, 0); Grid.SetRowSpan(col1Grid, 3);
        PrmViewerGrid.Children.Add(col1Grid);

        // ── Column 2: EFFECTS / VOICE ─────────────────────────────────────
        var fxCard = MakeSectionCard("REVERB", FxAccent, out var fxContent);

        // Reverb — 2-column compact grid
        var revItems = new List<Control>
        {
            MakePrmViewerCcRowCompact(RequireCC(91)),  // Level
            MakePrmViewerCcRowCompact(RequireCC(89)),  // Time
        };
        foreach (var p in _prm.ReverbMain) revItems.Add(MakePrmInfoRow(p, compact: true));
        foreach (var p in _prm.ReverbAdv)  revItems.Add(MakePrmInfoRow(p, compact: true));
        fxContent.Children.Add(MakeTwoColumnGrid(revItems));

        // Delay — 2-column compact grid
        fxContent.Children.Add(MakeSubSectionHeader("DELAY", FxAccent));
        var delItems = new List<Control>
        {
            MakePrmViewerCcRowCompact(RequireCC(92)),  // Level
            MakeDelayTimeViewerRow(),
        };
        foreach (var p in _prm.DelayMain) delItems.Add(MakePrmInfoRow(p, compact: true));
        delItems.Add(MakePrmInfoRow(_prm.DelayTempo, compact: true));
        foreach (var p in _prm.DelayAdv)  delItems.Add(MakePrmInfoRow(p, compact: true));
        fxContent.Children.Add(MakeTwoColumnGrid(delItems));

        // Chorus
        fxContent.Children.Add(MakeSubSectionHeader("CHORUS", FxAccent));
        fxContent.Children.Add(MakePrmViewerCcRow(RequireCC(93)));  // Chorus Type

        var voiceCard = MakeSectionCard("VOICE", VoiceAccent, out var voiceContent);
        var voiceItems = _patch.Controls.Concat(_patch.Voice)
            .Where(p => p.CcNumber != 65)
            .Select(p => (Control)MakePrmViewerCcRowCompact(p)).ToList();
        voiceContent.Children.Add(MakeTwoColumnGrid(voiceItems));

        var col2Grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 4 };
        Grid.SetRow(fxCard, 0);
        Grid.SetRow(voiceCard, 1);
        col2Grid.Children.Add(fxCard);
        col2Grid.Children.Add(voiceCard);
        Grid.SetColumn(col2Grid, 2); Grid.SetRow(col2Grid, 0); Grid.SetRowSpan(col2Grid, 3);
        PrmViewerGrid.Children.Add(col2Grid);

        // ── Row 3: SEQUENCER (full width) ────────────────────────────────
        var seqCard = MakeSectionCard("SEQUENCER", SeqAccent, out var seqContent);

        var seqMetaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing     = 20,
            Margin            = new Thickness(0, 0, 0, 8),
        };

        var patCol = new StackPanel { Spacing = 2 };
        patCol.Children.Add(MakeSubSectionHeader("PATTERN", SeqAccent));
        patCol.Children.Add(MakeInfoRow("Tempo:",     _tempoLabel));
        patCol.Children.Add(MakeInfoRow("Transpose:", _transposeLabel));
        patCol.Children.Add(MakePrmInfoRow(_prm.Leng));
        patCol.Children.Add(MakePrmInfoRow(_prm.Shuffle));
        patCol.Children.Add(MakePrmInfoRow(_prm.Level));
        patCol.Children.Add(MakePrmInfoRow(_prm.Scale));
        patCol.Children.Add(MakePrmInfoRow(_prm.TempoSync));
        Grid.SetColumn(patCol, 0);
        seqMetaGrid.Children.Add(patCol);

        var arpCol = new StackPanel { Spacing = 2 };
        arpCol.Children.Add(MakeSubSectionHeader("ARPEGGIATOR", SeqAccent));
        arpCol.Children.Add(MakePrmInfoRow(_prm.ArpType));
        arpCol.Children.Add(MakePrmInfoRow(_prm.ArpRate));
        Grid.SetColumn(arpCol, 1);
        seqMetaGrid.Children.Add(arpCol);

        var motCol = new StackPanel { Spacing = 2 };
        motCol.Children.Add(MakeSubSectionHeader("AUTOMATION", SeqAccent));
        for (int i = 0; i < 8; i++)
            motCol.Children.Add(MakeInfoRow($"Lane {i + 1}:", _motionCcLabels[i]));
        Grid.SetColumn(motCol, 2);
        seqMetaGrid.Children.Add(motCol);

        var dmCol = new StackPanel { Spacing = 2 };
        dmCol.Children.Add(MakeSubSectionHeader("D-MOTION", DmAccent));
        dmCol.Children.Add(MakePrmInfoRow(_prm.DmAssignX));
        dmCol.Children.Add(MakePrmInfoRow(_prm.DmAssignY));
        dmCol.Children.Add(MakePrmInfoRow(_prm.DmAssignTap));
        dmCol.Children.Add(MakePrmInfoRow(_prm.DmAssignFf));
        dmCol.Children.Add(MakePrmInfoRow(_prm.DmSensX));
        dmCol.Children.Add(MakePrmInfoRow(_prm.DmSensY));
        Grid.SetColumn(dmCol, 3);
        seqMetaGrid.Children.Add(dmCol);

        seqContent.Children.Add(seqMetaGrid);

        var seqBtnLabel = new TextBlock
        {
            FontSize      = 10.5,
            FontWeight    = FontWeight.SemiBold,
            LetterSpacing = 0.8,
        };
        var seqBtn = new Border
        {
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(4),
            Padding             = new Thickness(20, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = seqBtnLabel,
        };

        var seqAccentClr = ((SolidColorBrush)SeqAccent).Color;
        void RefreshSeqBtn()
        {
            bool has              = HasSequencerContent();
            seqBtn.IsEnabled      = has;
            seqBtn.Opacity        = has ? 1.0 : 0.35;
            seqBtn.Background     = has
                ? new SolidColorBrush(Color.FromArgb(0x18, seqAccentClr.R, seqAccentClr.G, seqAccentClr.B))
                : new SolidColorBrush(Color.Parse("#161616"));
            seqBtn.BorderBrush    = has ? SeqAccent : new SolidColorBrush(Color.Parse("#2E2E2E"));
            seqBtnLabel.Text       = "STEPS & AUTOMATION" + (has ? "  ↗" : "");
            seqBtnLabel.Foreground = has ? SeqAccent : new SolidColorBrush(Color.Parse("#555555"));
        }

        RefreshSeqBtn();
        _prm.Sequence.DataChanged += (_, _) => Dispatcher.UIThread.Post(RefreshSeqBtn);
        seqBtn.PointerPressed     += (_, _) => ShowSequencerWindow();

        seqContent.Children.Add(seqBtn);

        Grid.SetColumn(seqCard, 0); Grid.SetRow(seqCard, 3); Grid.SetColumnSpan(seqCard, 3);
        PrmViewerGrid.Children.Add(seqCard);
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
        var delaySw     = _prm.DelayMain[0];
        var delayTimeCC = RequireCC(90);

        var lbl = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")) };

        void Refresh()
        {
            lbl.Text = delaySw.Value == 0
                ? $"{1 + (int)Math.Round(delayTimeCC.Value * 739.0 / 127)}ms" // S-1 range: 1–740 ms
                : GetPrmDisplayString(_prm.DelayTempo);
        }

        Refresh();
        delaySw.ValueChanged     += (_, _) => Dispatcher.UIThread.Post(Refresh);
        delayTimeCC.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _prm.DelayTempo.ValueChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);

        return MakeInfoRow("Time:", lbl, labelMinWidth: 80);
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
                int signed = _prm.DrawWave.GetPoint(pt);
                // Re-encode as uint16 to split the two signed pad bytes (lo = pad 0, hi = pad 1).
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

        const double Gap    = 2.0;
        const double TotalW = 16 * BarW + 15 * Gap;
        const double TotalH = CellH * 2 + 1;

        var barsRow   = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap };
        var labelsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap,
                                         Margin = new Thickness(0, 2, 0, 0) };

        for (int i = 0; i < 16; i++)
        {
            var posBar = new Border { Width = BarW, Height = 0, Background = OscAccent,
                                      VerticalAlignment = VerticalAlignment.Bottom,
                                      CornerRadius = new CornerRadius(2, 2, 0, 0) };
            var negBar = new Border { Width = BarW, Height = 0, Background = OscAccent,
                                      VerticalAlignment = VerticalAlignment.Top,
                                      CornerRadius = new CornerRadius(0, 0, 2, 2) };
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

        // Faint horizontal reference lines at ±100%, ±50%, 0% levels.
        var lineColor       = new SolidColorBrush(Color.FromArgb(0x35, 0x88, 0x88, 0x88));
        var gridLinesCanvas = new Canvas { Width = TotalW, Height = TotalH, IsHitTestVisible = false };
        foreach (double y in new[] { 0.5, CellH / 2, CellH, CellH + 1 + CellH / 2, TotalH - 0.5 })
        {
            var gridLine = new Rectangle { Width = TotalW, Height = 1, Fill = lineColor };
            Canvas.SetTop(gridLine, y);
            gridLinesCanvas.Children.Add(gridLine);
        }

        // Layer grid lines behind bars in a single-cell Grid, then wrap in a styled Border.
        gridLinesCanvas.HorizontalAlignment = HorizontalAlignment.Left;
        gridLinesCanvas.VerticalAlignment   = VerticalAlignment.Top;
        barsRow.HorizontalAlignment         = HorizontalAlignment.Left;
        barsRow.VerticalAlignment           = VerticalAlignment.Top;

        var innerGrid = new Grid();
        innerGrid.Children.Add(gridLinesCanvas);
        innerGrid.Children.Add(barsRow);

        var barsContainer = new Border
        {
            Background      = new SolidColorBrush(Color.Parse("#1A1A1A")),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0x66, 0x38, 0x38, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(3),
            Child           = innerGrid,
        };

        UpdateBars();
        _prm.DrawWave.PointsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateBars);

        return new StackPanel { Children = { barsContainer, labelsRow } };
    }

    // Chop step-pattern grid display.
    private Panel MakeChopPatternControl()
    {
        var grid = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 2) };

        for (int w = 0; w < ChopPattern.Waveforms; w++)
        {
            int waveform    = w;
            var stepSquares = new Border[ChopPattern.Steps];

            // Fixed-height Grid row: label | separator | steps — guarantees vertical alignment.
            var row = new Grid
            {
                Height            = 18,
                ColumnDefinitions = new ColumnDefinitions("42,Auto,Auto"),
            };

            var rowLabel = new TextBlock
            {
                Text              = ChopPattern.WaveformNames[w],
                FontSize          = 10,
                Foreground        = new SolidColorBrush(Color.Parse("#BBBBBB")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rowLabel, 0);
            row.Children.Add(rowLabel);

            var sep = new Border
            {
                Width             = 1,
                Margin            = new Thickness(3, 2, 4, 2),
                Background        = new SolidColorBrush(Color.Parse("#2A2A38")),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetColumn(sep, 1);
            row.Children.Add(sep);

            var stepsContainer = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            for (int s = 0; s < ChopPattern.Steps; s++)
            {
                bool on = _prm.ChopPattern.GetStep(w, s);
                var sq = new Border
                {
                    Width           = 16,
                    Height          = 16,
                    Margin          = new Thickness(1, 0),
                    Background      = on ? s_chopOnBrush  : s_chopOffBrush,
                    BorderBrush     = on ? s_chopOnBorder : s_chopOffBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(2),
                };
                stepSquares[s] = sq;
                stepsContainer.Children.Add(sq);
            }
            Grid.SetColumn(stepsContainer, 2);
            row.Children.Add(stepsContainer);

            _prm.ChopPattern.PatternChanged += (_, changedWaveform) =>
            {
                if (changedWaveform != waveform) return;
                Dispatcher.UIThread.Post(() =>
                {
                    for (int s = 0; s < ChopPattern.Steps; s++)
                    {
                        bool on = _prm.ChopPattern.GetStep(waveform, s);
                        stepSquares[s].Background  = on ? s_chopOnBrush  : s_chopOffBrush;
                        stepSquares[s].BorderBrush = on ? s_chopOnBorder : s_chopOffBorder;
                    }
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

    private static StackPanel MakePrmInfoRow(PrmParameter p, bool compact = false)
    {
        var lbl = new TextBlock
        {
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            Text       = GetPrmDisplayString(p),
        };
        p.ValueChanged += (_, _) => Dispatcher.UIThread.Post(() => lbl.Text = GetPrmDisplayString(p));
        return MakeInfoRow(p.Name + ":", lbl, labelMinWidth: compact ? 80 : 130);
    }

    private static Grid MakeTwoColumnGrid(IList<Control> items)
    {
        int rows = (items.Count + 1) / 2;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, *"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows))),
            RowSpacing        = 2,
        };
        for (int i = 0; i < items.Count; i++)
        {
            Grid.SetRow(items[i], i / 2);
            Grid.SetColumn(items[i], i % 2);
            grid.Children.Add(items[i]);
        }
        return grid;
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

    private void OnDeviceDisconnected()
    {
        _isConnected                 = false;
        PatchGridContainer.IsEnabled = false;
        SendAllButton.IsEnabled      = false;
        ConnectButton.Content        = "Reconnect";
        SetStatus("Device disconnected.", "#FF6B6B");
        UpdateSyncIndicator(_patch.UnsyncedCount);
        ClearDirtyTracking();
    }

    private async Task PerformConnectAsync()
    {
        if (DeviceCombo.SelectedIndex < 0 || DeviceCombo.SelectedIndex >= _midiMgr.OutputPorts.Count)
        {
            SetStatus("Select a MIDI output device first.", "#FF6B6B");
            return;
        }

        _patch.ResetAllSync();

        var (outputOk, outName, inName, inputError) = await _midiMgr.ConnectAsync(
            DeviceCombo.SelectedIndex, InputCombo.SelectedIndex, MidiChannel);

        if (!outputOk)
        {
            SetStatus($"Output error: {inputError}", "#FF6B6B");
            return;
        }

        _isConnected                 = true;
        ConnectButton.Content        = "Reconnect";
        SendAllButton.IsEnabled      = true;
        PatchGridContainer.IsEnabled = true;
        UpdateSyncIndicator(_patch.UnsyncedCount);
        if (_prm.PatternSync)
            GoToPattern1();

        if (inputError != null)
            SetStatus($"→ {outName}  (input unavailable: {inputError})", "#F0A040");
        else if (inName != null)
            SetStatus($"↔ {outName}  |  listening on {inName}", "#70C870");
        else
            SetStatus($"→ {outName}  (no input selected)", "#70C870");
    }

    private async void TryAutoConnect()
    {
        int outIdx = _midiMgr.FindOutputIndex(n => n.Contains("S-1", StringComparison.OrdinalIgnoreCase));
        if (outIdx < 0)
        {
            SetStatus("Auto-connect: S-1 output not found.", "#F0A040");
            return;
        }

        int inIdx = _midiMgr.FindInputIndex(n => n.Contains("S-1", StringComparison.OrdinalIgnoreCase));

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
            if (doc.RootElement.TryGetProperty("autoConnect",      out var autoConnectEl))      _autoConnect                = autoConnectEl.GetBoolean();
            if (doc.RootElement.TryGetProperty("filterModEnabled", out var filterModEl))       _viewModel.FilterModEnabled = filterModEl.GetBoolean();
            if (doc.RootElement.TryGetProperty("prmFolder",        out var prmFolderEl))       _prm.PrmFolder              = prmFolderEl.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("patternSync",      out var patternSyncEl))     _prm.PatternSync            = patternSyncEl.GetBoolean();
            if (doc.RootElement.TryGetProperty("midiChannel",      out var midiChannelEl))     _midiChannel                = Math.Clamp(midiChannelEl.GetInt32(), 1, 16);
            if (doc.RootElement.TryGetProperty("pcChannel",        out var pcChannelEl))       _pcChannel                  = Math.Clamp(pcChannelEl.GetInt32(), 1, 16);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load failed: {ex.Message}");
            SetStatus("Settings failed to load — using defaults.", "#F0A040");
        }
    }

    private void SaveSettings()
    {
        try
        {
            System.IO.File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
            {
                autoConnect      = _autoConnect,
                filterModEnabled = _viewModel.FilterModEnabled,
                prmFolder        = _prm.PrmFolder,
                patternSync      = _prm.PatternSync,
                midiChannel      = MidiChannel,
                pcChannel        = PcChannel,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            SetStatus("Settings could not be saved.", "#F0A040");
        }
    }

    private async void OnSendAllClicked(object? sender, RoutedEventArgs e)
    {
        SendAllButton.IsEnabled = false;
        SetStatus("Initializing…", "#AAAAAA");
        _prm.ApplyInitPatch();
        await _patch.SendAllAsync();
        _patch.MarkAllSynced();
        SendAllButton.IsEnabled = true;
        SetStatus("Settings initialized.", "#70C870");
    }

    // ── Preset save / load ────────────────────────────────────────────────────

    private static readonly FilePickerFileType S1PatchFileType =
        new("S-1 Patch") { Patterns = new[] { "*.s1patch" } };

    private static readonly FilePickerFileType PrmFileType =
        new("S-1 PRM Patch") { Patterns = new[] { "*.PRM", "*.prm" } };

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
        preset.PrmOnly = _prm.AllPrmOnlyParams()
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
            var prmLookup = _prm.AllPrmOnlyParams().ToDictionary(p => p.PrmKey);
            foreach (var entry in preset.PrmOnly)
            {
                if (prmLookup.TryGetValue(entry.PrmKey, out var prm))
                    prm.Value = entry.Value;
            }
        }

        await _patch.SendAllAsync();
        SetStatus($"Loaded: {preset.Name}", "#70C870");
    }

    private async Task<bool> ShowPatternSyncWarningAsync()
    {
        bool confirmed = false;

        var yesBtn = new Button { Content = "Enable Patch Mirror", Classes = { "toolbar" } };
        var noBtn  = new Button { Content = "Cancel",             Classes = { "toolbar" } };

        var dlg = new Window
        {
            Title                 = "Patch Mirror: Safety Warning",
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
                            "When Patch Mirror is enabled, switching patterns (by clicking a button " +
                            "in the editor or by pressing a pattern button on the S-1) will " +
                            "automatically load the corresponding PRM file from your PRM folder " +
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

        _prm.PrmFolder    = folders[0].TryGetLocalPath() ?? "";
        PrmFolderBox.Text = _prm.PrmFolder;
        RefreshLiveFeaturesState();
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
            var localPath = files[0].TryGetLocalPath()!;
            parsed = await Task.Run(() => PrmFileParser.Parse(localPath));
        }
        catch (Exception ex)
        {
            SetStatus($"PRM load error: {ex.Message}", "#FF6B6B");
            return;
        }

        _prm.ApplyPrmData(parsed);

        var fileName = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
        PresetNameBox.Text = fileName;

        SetStatus("Sending PRM values…", "#AAAAAA");
        await _patch.SendAllAsync();
        SetStatus($"Loaded PRM: {fileName}", "#70C870");
    }

    private void OnPrmMetaLoaded(object? sender, PrmMetaArgs e)
    {
        _tempoLabel.Text     = e.Tempo;
        _transposeLabel.Text = e.Transpose;
        for (int i = 0; i < 8; i++)
            _motionCcLabels[i].Text = e.MotionCcLabels[i];
    }

    private void SetStatus(string message, string hexColour)
    {
        StatusText.Text       = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(hexColour));
    }

    private void UpdateSyncIndicator(int unsyncedCount)
    {
        if (!_isConnected)
        {
            SyncIndicatorText.Text = "";
            return;
        }
        if (unsyncedCount <= 0)
        {
            SyncIndicatorText.Text       = "✓ Synced";
            SyncIndicatorText.Foreground = new SolidColorBrush(Color.Parse("#70C870"));
        }
        else
        {
            SyncIndicatorText.Text       = $"⚠ {unsyncedCount} unsynced";
            SyncIndicatorText.Foreground = new SolidColorBrush(Color.Parse("#F0A040"));
        }
    }

    private void GoToPattern1()
    {
        _patch.SendProgramChange(0, PcChannel);
        HighlightPatchButton(0);
    }

    // ── Filter modulation: 60 fps tick ───────────────────────────────────────

    private void OnModTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastModTick).TotalSeconds, 0.1);
        _lastModTick = now;

        bool midiLit = (now - _lastMidiActivity).TotalMilliseconds < 150;
        if (midiLit != _midiDotLit) { _midiDotLit = midiLit; MidiActivityDot.Background = midiLit ? MidiDotActive : MidiDotIdle; }

        if (_viewModel.Tick(dt))
        {
            _filterCurveUpdate?.Invoke();
            _envelopeDotUpdate?.Invoke();
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    // ── Aspect-ratio enforcement (Win32 WM_SIZING hook) ──────────────────────

    private const int  GWLP_WNDPROC      = -4;
    private const uint WM_SIZING         = 0x0214;
    private const int  WMSZ_LEFT         = 1, WMSZ_RIGHT        = 2;
    private const int  WMSZ_TOP          = 3, WMSZ_TOPLEFT      = 4, WMSZ_TOPRIGHT    = 5;
    private const int  WMSZ_BOTTOM       = 6, WMSZ_BOTTOMLEFT   = 7, WMSZ_BOTTOMRIGHT = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void FitToScreen()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        double maxW = screen.WorkingArea.Width  / screen.Scaling;
        double maxH = screen.WorkingArea.Height / screen.Scaling;
        if (Width > maxW || Height > maxH)
        {
            double scale = Math.Min(maxW / DesignWidth, maxH / DesignHeight);
            Width  = Math.Round(DesignWidth  * scale);
            Height = Math.Round(DesignHeight * scale);
        }
    }

    private void HookAspectRatio()
    {
        var handle = TryGetPlatformHandle();
        if (handle is null) return;
        _arWndProcDelegate = WndProcHook;
        _arOldWndProc = SetWindowLongPtrW(handle.Handle, GWLP_WNDPROC,
                            Marshal.GetFunctionPointerForDelegate(_arWndProcDelegate));
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SIZING)
        {
            var rect = Marshal.PtrToStructure<Win32Rect>(lParam);
            int edge = (int)wParam;
            int w    = rect.Right  - rect.Left;
            int h    = rect.Bottom - rect.Top;

            // Pure top/bottom drag: lock height, adjust width rightward.
            // All other edges (including corners): lock width, adjust height.
            bool pureVertical = edge is WMSZ_TOP or WMSZ_BOTTOM;
            if (pureVertical)
            {
                rect.Right = rect.Left + (int)Math.Round(h * AspectRatio);
            }
            else
            {
                int newH = (int)Math.Round(w / AspectRatio);
                bool topDriven = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT;
                if (topDriven) rect.Top    = rect.Bottom - newH;
                else           rect.Bottom = rect.Top    + newH;
            }

            Marshal.StructureToPtr(rect, lParam, false);
        }
        return CallWindowProcW(_arOldWndProc, hWnd, msg, wParam, lParam);
    }

    protected override void OnClosed(EventArgs e)
    {
        _modTimer.Stop();
        _midiMgr.Dispose();
        _patch.Dispose();
        base.OnClosed(e);
    }
}
