using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;
using S1Utility.Midi;

namespace S1Utility;

public partial class MainWindow : Window
{
    private readonly S1Patch        _patch = new();
    private readonly MidiManager    _midiMgr;
    private readonly PrmFileManager _prm;
    private readonly EnvelopeAnimator _envAnimator;

    private int MidiChannel => ChannelCombo.SelectedIndex >= 0 ? ChannelCombo.SelectedIndex + 1 : 3;
    private int PcChannel   => ProgramChangeChannelCombo.SelectedIndex >= 0 ? ProgramChangeChannelCombo.SelectedIndex + 1 : 16;
    private int MirrorInitialProgram =>
        Math.Clamp(MirrorInitialBankCombo.SelectedIndex,    0, 3)  * 16
      + Math.Clamp(MirrorInitialPatternCombo.SelectedIndex, 0, 15);

    // Patch/pattern bank buttons (4 groups × 16 patterns = 64 program changes).
    private readonly List<Button> _patchButtons = new();

    // Dirty patch tracking — only active when Patch Mirror is on.
    private readonly Dictionary<int, int[]> _slotSnapshots       = new(); // clean PRM baseline per slot
    private readonly Dictionary<int, int[]> _dirtyStateSnapshots = new(); // user-modified state per slot
    private readonly HashSet<int>           _dirtySlots          = new(); // slots modified since load
    private int    _currentSlotIndex      = -1;
    private bool   _suppressDirtyTracking;
    private Button? _restorePatchButton;
    private Button? _initPatchButton;

    // Tab-2-only PRM control (created in BuildPatchPanel, visibility toggled by MainTabs).
    private Button? OpenPrmButton;
    private Panel?  _initRestoreGroup;

    private bool   _autoConnect;
    private bool   _isConnected;
    private bool   _patternSyncDialogOpen;
    private bool   _skipPatternSyncWarning;
    private string _outPortName = "";
    private Window? _seqWindow;
    private Window? _settingsWindow;
    private Window? _infoWindow;
    private int    _midiChannel  = 3;
    private int    _pcChannel    = 16;
    private int    _mirrorInitialProgram;
    private bool   _mirrorForcePushOnPc;

    // Settings popup controls — declared here so they survive the popup being closed
    // and can be re-parented next time it opens. Populated/wired in PopulateDeviceLists
    // and the constructor, exactly as the equivalent x:Name'd XAML controls used to be.
    private readonly ComboBox ChannelCombo = new()
    {
        Height      = 26,
        FontSize    = 11,
        Background  = new SolidColorBrush(Color.Parse("#1E1E28")),
        BorderBrush = new SolidColorBrush(Color.Parse("#3A3A46")),
    };
    private readonly ComboBox ProgramChangeChannelCombo = new()
    {
        Height      = 26,
        FontSize    = 11,
        Background  = new SolidColorBrush(Color.Parse("#1E1E28")),
        BorderBrush = new SolidColorBrush(Color.Parse("#3A3A46")),
    };
    private readonly ComboBox MirrorInitialBankCombo = new()
    {
        Height      = 26,
        FontSize    = 11,
        Background  = new SolidColorBrush(Color.Parse("#1E1E28")),
        BorderBrush = new SolidColorBrush(Color.Parse("#3A3A46")),
    };
    private readonly ComboBox MirrorInitialPatternCombo = new()
    {
        Height      = 26,
        FontSize    = 11,
        Background  = new SolidColorBrush(Color.Parse("#1E1E28")),
        BorderBrush = new SolidColorBrush(Color.Parse("#3A3A46")),
    };
    private readonly TextBox PrmFolderBox = new()
    {
        Height          = 26,
        FontSize        = 11,
        IsReadOnly      = true,
        PlaceholderText = "(not set)",
        Background      = new SolidColorBrush(Color.Parse("#1A1A24")),
        BorderBrush     = new SolidColorBrush(Color.Parse("#333344")),
        Foreground      = new SolidColorBrush(Color.Parse("#9090A8")),
        CornerRadius    = new CornerRadius(3),
    };
    private readonly Button BrowsePrmFolderButton = new()
    {
        Content  = "Browse…",
        Classes  = { "toolbar" },
    };
    private readonly CheckBox AutoConnectCheckBox = new()
    {
        Content    = "Attempt auto-connect on app start",
        FontSize   = 11,
        Foreground = new SolidColorBrush(Color.Parse("#A0A0B8")),
    };
    private readonly CheckBox MirrorForcePushCheckBox = new()
    {
        Content    = "Force editor values on Program Change",
        FontSize   = 11,
        Foreground = new SolidColorBrush(Color.Parse("#A0A0B8")),
    };

    private sealed record HeuristicToggleState(Action<bool> SetActive, Action<bool> SetEnabled);

    private HeuristicToggleState? _patchMirrorToggle;
    private HeuristicToggleState? _animationsToggle;

    // ── Animation update delegates ────────────────────────────────────────────
    //
    // Wired from the visualiser builders (filter curve, ADSR, OSC waveform).
    // Invoked from OnModTimerTick at 60 Hz and from event boundaries (LFO
    // trigger-mode change, Animations toggle, Patch Mirror off) to flush state.

    private Action?  _filterCurveUpdate;
    private Action?  _envelopeDotUpdate;
    private Action?  _envelopeOverlayUpdate;
    private Action?  _oscWaveformUpdate;
    private DateTime _lastModTick;
    private DateTime _lastMidiActivity = DateTime.MinValue;
    private bool     _midiDotLit;
    private readonly DispatcherTimer _modTimer = new();

    // ── Aspect-ratio scaling ──────────────────────────────────────────────────

    private const  double DesignWidth  = 1100;
    private const  double DesignHeight = 1040;
    private const  double AspectRatio  = DesignWidth / DesignHeight;

    private IWindowSizePolicy? _sizePolicy;

    // Labels for values that need special formatting (tempo as BPM, motion CC names)
    private readonly TextBlock   _tempoLabel     = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };
    private readonly TextBlock[] _motionCcLabels = new TextBlock[8];

    // Section accent colours forward to Palette (which resolves them from
    // Application.Resources). Kept as named getters so Builders/Sequencer call
    // sites stay terse — but the canonical hex values now live only in App.axaml.
    private static IBrush OscAccent   => Palette.AccentOsc;
    private static IBrush SeqAccent   => Palette.AccentSeq;
    private static IBrush FiltAccent  => Palette.AccentFilter;
    private static IBrush EnvAccent   => Palette.AccentEnv;
    private static IBrush LfoAccent   => Palette.AccentLfo;
    private static IBrush VoiceAccent => Palette.AccentVoice;
    private static IBrush FxAccent    => Palette.AccentFx;
    private static IBrush DmAccent    => Palette.AccentDm;
    private static IBrush WarnBrush   => Palette.AccentWarn;

    public MainWindow()
    {
        Log.Logger = new RollingFileLogger(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "S1Utility", "logs"));

        // Only marshal when we're NOT already on the UI thread. Posting
        // unconditionally would defer ValueChanged events fired inside
        // synchronous UI-thread code (e.g. PRM load), breaking patterns like
        // _suppressDirtyTracking = true; load; _suppressDirtyTracking = false
        // — the suppress flag would lapse before the queued callbacks ran.
        _patch.UiDispatcher = action =>
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        };

        _envAnimator = new EnvelopeAnimator(_patch);
        _prm         = new PrmFileManager(_patch);

        // Backend picked by platform. DryWetMidi handles Win/Mac (WinMM /
        // CoreMIDI); managed-midi handles Linux (ALSA sequencer client).
        IS1MidiBackend backend = OperatingSystem.IsLinux()
            ? new Midi.Linux.AlsaMidiBackend()
            : new DryWetMidiBackend();
        _midiMgr = new MidiManager(_patch, backend);

        _prm.MetaLoaded               += OnPrmMetaLoaded;
        _prm.StatusChanged            += (_, args) => SetStatus(args.Message, args.Kind);
        _prm.PatchAvailabilityChanged += (_, _) => RefreshAllPatchButtonStyles();

        _midiMgr.Disconnected          += (_, _) => Dispatcher.UIThread.Post(OnDeviceDisconnected);
        _midiMgr.NoteOnReceived        += (_, _) => Dispatcher.UIThread.Post(_envAnimator.NoteOn);
        _midiMgr.NoteOffReceived       += (_, _) => Dispatcher.UIThread.Post(_envAnimator.NoteOff);
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
            {
                _sizePolicy = new WindowsAspectRatioPolicy(AspectRatio);
                _sizePolicy.Attach(this);
            }
            else if (OperatingSystem.IsLinux())
            {
                _sizePolicy = new LinuxAspectRatioPolicy(AspectRatio);
                _sizePolicy.Attach(this);
            }
            // X11 only delivers key events to a window once a focusable element
            // inside it holds focus. Without this, the global arrow/shortcut
            // handler never fired on Linux until the user first clicked a patch
            // tile (which calls Focus()). Focusing the window on open makes
            // keyboard nav work from launch; Windows delivered these regardless.
            Focus();
        };

        LoadSettings();
        PopulateDeviceLists();
        BuildRealtimeEditorPanels();
        BuildPrmViewerContent();
        _prm.ApplyInitPatch();

        ConnectButton.Click          += OnConnectClicked;
        PanicButton.Click            += OnPanicClicked;
        UndoButton.Click             += OnUndoClicked;
        RedoButton.Click             += OnRedoClicked;
        OpenPrmButton!.Click         += OnOpenPrmClicked;
        MainTabs.SelectionChanged    += (_, _) =>
        {
            bool onTab2 = MainTabs.SelectedIndex == 1;
            // Bodies overlap in TabBodyHost; toggle visibility without collapsing
            // layout (Opacity, not IsVisible) so the host height stays put and the
            // toolbar above does not shift on tab change.
            RealtimeBody.Opacity           = onTab2 ? 0 : 1;
            RealtimeBody.IsHitTestVisible  = !onTab2;
            InspectorBody.Opacity          = onTab2 ? 1 : 0;
            InspectorBody.IsHitTestVisible = onTab2;
            // Tab 1 (Realtime Editor): Init / Restore. Tab 2 (Patch Inspector, read-only): Open PRM.
            if (_initRestoreGroup != null) _initRestoreGroup.IsVisible = !onTab2;
            OpenPrmButton.IsVisible = onTab2;
            UpdatePatchGridAvailability();
        };
        BrowsePrmFolderButton.Click  += OnBrowsePrmFolderClicked;
        SettingsButton.Click         += OnSettingsClicked;
        InfoButton.Click             += OnInfoClicked;
        SupportButton.Click          += OnSupportClicked;

        AutoConnectCheckBox.IsChecked = _autoConnect;
        AutoConnectCheckBox.IsCheckedChanged += (_, _) =>
        {
            _autoConnect = AutoConnectCheckBox.IsChecked == true;
            SaveSettings();
        };

        MirrorForcePushCheckBox.IsChecked = _mirrorForcePushOnPc;
        MirrorForcePushCheckBox.IsCheckedChanged += (_, _) =>
        {
            _mirrorForcePushOnPc = MirrorForcePushCheckBox.IsChecked == true;
            SaveSettings();
        };

        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        UpdatePatchGridAvailability();
        UpdateInspectorBanner();
        // Initial scan: paints noprm/malformed states from the folder loaded in
        // settings. RescanFolder fires PatchAvailabilityChanged → RefreshAllPatchButtonStyles.
        _prm.RescanFolder();

        PrmFolderBox.Text = _prm.PrmFolder;
        BuildLiveFeaturesPanel();
        UpdateSyncIndicator(_patch.UnsyncedCount);
        if (_autoConnect) TryAutoConnect();

        // Subscribe after ApplyInitPatch so the init run does not trigger dirty marks
        // or seed undo entries before the user has done anything.
        foreach (var param in _patch.AllParameters)
            param.ValueChanged += (_, _) => MarkCurrentSlotDirty();
        InitializeUndoTracking();

        _lastModTick = DateTime.UtcNow;
        _modTimer.Interval = TimeSpan.FromMilliseconds(16);
        _modTimer.Tick += OnModTimerTick;
        _modTimer.Start();
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift)   != 0;

        if (ctrl && e.Key == Key.S)
        {
            OnSaveClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Don't hijack Ctrl+Z/Y inside a text field — keep native text undo.
        bool inTextBox = FocusManager?.GetFocusedElement() is TextBox;

        if (!inTextBox && ctrl && e.Key == Key.Z && !shift)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (!inTextBox && ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (!inTextBox && ctrl && e.Key == Key.M)
        {
            OnConnectClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!inTextBox && ctrl && e.Key == Key.I && _initPatchButton?.IsEnabled == true)
        {
            OnInitPatchClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!inTextBox && ctrl && e.Key == Key.R && _restorePatchButton?.IsEnabled == true)
        {
            OnRestorePatchClicked();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            // Preserve focus traversal inside text fields.
            if (FocusManager?.GetFocusedElement() is TextBox) return;

            int count = MainTabs.ItemCount;
            if (count <= 0) return;
            int dir = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1;
            MainTabs.SelectedIndex = (MainTabs.SelectedIndex + dir + count) % count;
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var focused = FocusManager?.GetFocusedElement();

            // Tab strip: suppress arrows so they don't switch tabs.
            if (focused is TabItem) { e.Handled = true; return; }

            // Don't hijack arrows while editing in input controls.
            if (focused is TextBox or ComboBox or Slider) return;

            if (!PatchGridContainer.IsEnabled) return;
            OnPatchGridKeyDown(this, e);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _sizePolicy?.Detach();
        _modTimer.Stop();
        _midiMgr.Dispose();
        _patch.Dispose();
        (Log.Logger as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    // ── Window sizing ────────────────────────────────────────────────────────
    //
    // Aspect-ratio enforcement during resize is platform-specific and lives in
    // `IWindowSizePolicy` / `WindowsAspectRatioPolicy`. `FitToScreen` is the
    // cross-platform piece that runs on Opened.

    private void FitToScreen()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        double maxW = screen.WorkingArea.Width  / screen.Scaling;
        double maxH = screen.WorkingArea.Height / screen.Scaling;

        // Reserve room for the window's title bar and borders. FrameSize includes
        // decorations, ClientSize does not, so their difference is the chrome the
        // WM draws around us. On X11 that height is added after we set Height, so
        // without this allowance the bottom of the page falls below the work area
        // and the last cards get clipped (the Viewbox can't recover space that is
        // off-screen). Fall back to a conservative title-bar estimate when the
        // platform hasn't reported a frame size yet.
        double frameExtraW = 0, frameExtraH = 0;
        if (FrameSize is { } fs && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            frameExtraW = Math.Max(0, fs.Width  - ClientSize.Width);
            frameExtraH = Math.Max(0, fs.Height - ClientSize.Height);
        }
        if (frameExtraH <= 0) frameExtraH = 48;

        double availW = maxW - frameExtraW;
        double availH = maxH - frameExtraH;

        // Scale down whenever the design size (at its locked aspect ratio) cannot
        // fit the available area, not only when the raw width/height exceed it.
        double scale = Math.Min(availW / DesignWidth, availH / DesignHeight);
        if (scale < 1)
        {
            Width  = Math.Round(DesignWidth  * scale);
            Height = Math.Round(DesignHeight * scale);
        }
    }
}
