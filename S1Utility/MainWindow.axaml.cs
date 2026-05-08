using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
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
    private Window? _seqWindow;
    private int    _midiChannel  = 3;
    private int    _pcChannel    = 3;

    private sealed record HeuristicToggleState(Action<bool> SetActive, Action<bool> SetEnabled);

    private HeuristicToggleState? _patchMirrorToggle;
    private HeuristicToggleState? _liveViewToggle;

    // ── Filter modulation animation ───────────────────────────────────────────

    private Action?  _filterCurveUpdate;
    private Action?  _envelopeDotUpdate;
    private DateTime _lastModTick;
    private DateTime _lastMidiActivity = DateTime.MinValue;
    private bool     _midiDotLit;
    private readonly DispatcherTimer _modTimer = new();

    // ── Aspect-ratio scaling ──────────────────────────────────────────────────

    private const  double DesignWidth  = 1100;
    private const  double DesignHeight = 1100;
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
        PanicButton.Click            += OnPanicClicked;
        SaveButton.Click             += OnSaveClicked;
        LoadButton.Click             += OnLoadClicked;
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
        };
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

    protected override void OnClosed(EventArgs e)
    {
        _modTimer.Stop();
        _midiMgr.Dispose();
        _patch.Dispose();
        base.OnClosed(e);
    }
}
