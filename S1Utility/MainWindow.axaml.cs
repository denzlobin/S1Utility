using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using S1Utility.Core;

namespace S1Utility;

public partial class MainWindow : Window
{
    private readonly S1Patch        _patch = new();
    private readonly MidiManager    _midiMgr;
    private readonly PrmFileManager _prm;
    private readonly S1EditorViewModel _viewModel;

    private int MidiChannel => ChannelCombo.SelectedIndex >= 0 ? ChannelCombo.SelectedIndex + 1 : 3;
    private int PcChannel   => ProgramChangeChannelCombo.SelectedIndex >= 0 ? ProgramChangeChannelCombo.SelectedIndex + 1 : 16;

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

    // Tab-2-only PRM controls (created in BuildPatchPanel, visibility toggled by MainTabs).
    private Button?    OpenPrmButton;
    private Button?    PrmInfoToggle;
    private TextBlock? PrmInfoText;
    private StackPanel? _initRestoreGroup;

    private bool   _autoConnect;
    private bool   _isConnected;
    private bool   _patternSyncDialogOpen;
    private bool   _skipPatternSyncWarning;
    private string _outPortName = "";
    private Window? _seqWindow;
    private Window? _settingsWindow;
    private int    _midiChannel  = 3;
    private int    _pcChannel    = 3;

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

    private sealed record HeuristicToggleState(Action<bool> SetActive, Action<bool> SetEnabled);

    private HeuristicToggleState? _patchMirrorToggle;
    private HeuristicToggleState? _liveViewToggle;

    // ── Filter modulation animation ───────────────────────────────────────────

    private Action?  _filterCurveUpdate;
    private Action?  _envelopeDotUpdate;
    private Action?  _envelopeWarningUpdate;
    private Action?  _oscWaveformUpdate;
    private DateTime _lastModTick;
    private DateTime _lastMidiActivity = DateTime.MinValue;
    private bool     _midiDotLit;
    private readonly DispatcherTimer _modTimer = new();

    // ── Aspect-ratio scaling ──────────────────────────────────────────────────

    private const  double DesignWidth  = 1100;
    private const  double DesignHeight = 1130;
    private const  double AspectRatio  = DesignWidth / DesignHeight;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _arWndProcDelegate;
    private IntPtr           _arOldWndProc;

    // Labels for values that need special formatting (tempo as BPM, signed transpose, motion CC names)
    private readonly TextBlock   _tempoLabel     = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };
    private readonly TextBlock   _transposeLabel = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), Text = "—" };
    private readonly TextBlock[] _motionCcLabels = new TextBlock[8];

    // ── Section accent colours (shared across builders / sequencer) ───────────

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

        _prm.MetaLoaded               += OnPrmMetaLoaded;
        _prm.StatusChanged            += (_, args) => SetStatus(args.Message, args.Color);
        _prm.PatchAvailabilityChanged += (_, _) => RefreshAllPatchButtonStyles();

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
        PanicButton.Click            += OnPanicClicked;
        SaveButton.Click             += OnSaveClicked;
        LoadButton.Click             += OnLoadClicked;
        UndoButton.Click             += OnUndoClicked;
        RedoButton.Click             += OnRedoClicked;
        OpenPrmButton!.Click         += OnOpenPrmClicked;
        PrmInfoToggle!.Click         += (_, _) => PrmInfoText!.IsVisible = !PrmInfoText.IsVisible;
        MainTabs.SelectionChanged    += (_, _) =>
        {
            bool onTab2 = MainTabs.SelectedIndex == 1;
            // Tab 1 (Realtime Editor): Init / Restore. Tab 2 (Patch Inspector, read-only): Open PRM / info.
            if (_initRestoreGroup != null) _initRestoreGroup.IsVisible = !onTab2;
            OpenPrmButton.IsVisible = onTab2;
            PrmInfoToggle.IsVisible = onTab2;
            if (!onTab2 && PrmInfoText != null) PrmInfoText.IsVisible = false;
            UpdatePatchGridAvailability();
        };
        BrowsePrmFolderButton.Click  += OnBrowsePrmFolderClicked;
        SettingsButton.Click         += OnSettingsClicked;

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
        _modTimer.Stop();
        _midiMgr.Dispose();
        _patch.Dispose();
        base.OnClosed(e);
    }
}
