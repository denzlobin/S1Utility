using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using S1Utility.Core;

namespace S1Utility;

// MIDI device enumeration, connect/disconnect lifecycle, and the status surfaces
// (footer dot, sync indicator, channel labels) that follow connection state.
// `OnModTimerTick` lives here because its primary job is the MIDI activity dot;
// the viewmodel-tick fan-out at the bottom is the secondary concern.
public partial class MainWindow
{
    private static IBrush MidiDotActive => Palette.AccentOsc;
    private static readonly IBrush MidiDotIdle = new SolidColorBrush(Color.Parse("#2C2C3A"));

    // Off-state dot is a one-off dark fill not shared with any other surface; kept local.
    private static readonly IBrush s_footerDotOff = new SolidColorBrush(Color.Parse("#2A2A33"));

    // ── Device lists ──────────────────────────────────────────────────────────

    private void PopulateDeviceLists()
    {
        foreach (var name in _midiMgr.OutputDeviceNames)
            DeviceCombo.Items.Add(name);

        foreach (var name in _midiMgr.InputDeviceNames)
            InputCombo.Items.Add(name);

        for (int ch = 1; ch <= 16; ch++)
        {
            ChannelCombo.Items.Add(ch.ToString());
            ProgramChangeChannelCombo.Items.Add(ch.ToString());
        }
        for (int bank = 1; bank <= 4; bank++)
            MirrorInitialBankCombo.Items.Add($"Bank {bank}");
        for (int pattern = 1; pattern <= 16; pattern++)
            MirrorInitialPatternCombo.Items.Add($"Pattern {pattern:D2}");

        ChannelCombo.SelectedIndex              = _midiChannel - 1;
        ProgramChangeChannelCombo.SelectedIndex = _pcChannel   - 1;
        MirrorInitialBankCombo.SelectedIndex    = _mirrorInitialProgram / 16;
        MirrorInitialPatternCombo.SelectedIndex = _mirrorInitialProgram % 16;
        UpdateFooterChannels();

        ChannelCombo.SelectionChanged += (_, _) => { SaveSettings(); UpdateFooterChannels(); };
        ProgramChangeChannelCombo.SelectionChanged += (_, _) => { SaveSettings(); UpdateFooterChannels(); };

        void OnMirrorPatchChanged(object? _, SelectionChangedEventArgs __)
        {
            _mirrorInitialProgram = MirrorInitialProgram;
            SaveSettings();
        }
        MirrorInitialBankCombo.SelectionChanged    += OnMirrorPatchChanged;
        MirrorInitialPatternCombo.SelectionChanged += OnMirrorPatchChanged;

        DeviceCombo.DropDownOpened += (_, _) => ReenumerateDevices();
        InputCombo.DropDownOpened  += (_, _) => ReenumerateDevices();

        DeviceCombo.SelectedIndex = PreferredOutputIndex();
        InputCombo.SelectedIndex  = PreferredInputIndex();
    }

    // Pre-select an S-1 device when available so the dropdown is useful out of the box
    // even when auto-connect is off. Falls back to index 0 (first OS-enumerated device).
    private int PreferredOutputIndex()
    {
        if (_midiMgr.OutputDeviceNames.Count == 0) return -1;
        int idx = _midiMgr.FindOutputIndex(n => n.Contains("S-1", StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : 0;
    }

    private int PreferredInputIndex()
    {
        if (_midiMgr.InputDeviceNames.Count == 0) return -1;
        int idx = _midiMgr.FindInputIndex(n => n.Contains("S-1", StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : 0;
    }

    private void ReenumerateDevices()
    {
        string? prevOut = DeviceCombo.SelectedIndex >= 0
            ? DeviceCombo.Items[DeviceCombo.SelectedIndex] as string : null;
        string? prevIn = InputCombo.SelectedIndex >= 0
            && InputCombo.SelectedIndex < _midiMgr.InputDeviceNames.Count
            ? _midiMgr.InputDeviceNames[InputCombo.SelectedIndex] : null;

        _midiMgr.EnumerateDevices();

        DeviceCombo.Items.Clear();
        foreach (var name in _midiMgr.OutputDeviceNames)
            DeviceCombo.Items.Add(name);

        InputCombo.Items.Clear();
        foreach (var name in _midiMgr.InputDeviceNames)
            InputCombo.Items.Add(name);

        // Restore previous selection by name; if the user had no prior selection
        // (or the device is gone), fall back to the preferred S-1-first index.
        if (prevOut != null)
        {
            int idx = DeviceCombo.Items.Cast<string>().ToList().IndexOf(prevOut);
            DeviceCombo.SelectedIndex = idx >= 0 ? idx : PreferredOutputIndex();
        }
        else
            DeviceCombo.SelectedIndex = PreferredOutputIndex();

        if (prevIn != null)
        {
            int idx = _midiMgr.FindInputIndex(n => n == prevIn);
            InputCombo.SelectedIndex = idx >= 0 ? idx : PreferredInputIndex();
        }
        else
            InputCombo.SelectedIndex = PreferredInputIndex();
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private async void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_isConnected)
            PerformDisconnect();
        else
            await PerformConnectAsync();
    }

    // Involuntary disconnect: transport SendEvent failed and the MidiManager fired
    // its Disconnected event. Treat as an error condition for the user.
    private void OnDeviceDisconnected() =>
        ApplyDisconnectedState("Device disconnected.", StatusKind.Error);

    // User-initiated disconnect from the Connect/Disconnect toggle. Stop the input
    // listener so the next Connect starts from a clean state, then mirror the
    // involuntary-disconnect UI cleanup.
    private void PerformDisconnect()
    {
        _midiMgr.Disconnect();
        ApplyDisconnectedState("Disconnected.", StatusKind.Info);
    }

    private void ApplyDisconnectedState(string status, StatusKind kind)
    {
        _isConnected                 = false;
        _outPortName                 = "";
        _initPatchButton!.IsEnabled  = false;
        PanicButton.IsEnabled        = false;
        ConnectButton.Content        = "Connect";
        SetStatus(status, kind);
        // Null the transport so further knob drags don't throw inside the stale
        // DryWetMidiTransport and get silently swallowed.
        _patch.Disconnect();
        // Without the synth we no longer know any parameter's state — gray every
        // knob and show "?" in value labels. Honest about what it knows.
        _patch.ResetAllSync();
        UpdateSyncIndicator(_patch.UnsyncedCount);
        ClearDirtyTracking();
        UpdatePatchGridAvailability();
    }

    private async Task PerformConnectAsync()
    {
        // Re-enumerate first so unplug/replug recovers without a separate refresh step.
        ReenumerateDevices();

        if (DeviceCombo.SelectedIndex < 0 || DeviceCombo.SelectedIndex >= _midiMgr.OutputDeviceNames.Count)
        {
            SetStatus("Select a MIDI output device first.", StatusKind.Error);
            return;
        }

        // Foot-gun guard: if the user clicks Connect with the S-1 powered off but
        // another MIDI device present, the dropdown fell back to that other device.
        // Confirm before we silently start sending CC traffic to the wrong target.
        var pickedName = _midiMgr.OutputDeviceNames[DeviceCombo.SelectedIndex];
        if (!pickedName.Contains("S-1", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = await ShowNonS1ConnectWarningAsync(pickedName);
            if (!ok)
            {
                SetStatus("Connect cancelled.", StatusKind.Info);
                return;
            }
        }

        _patch.ResetAllSync();

        var (outputOk, outName, inName, inputError) = await _midiMgr.ConnectAsync(
            DeviceCombo.SelectedIndex, InputCombo.SelectedIndex, MidiChannel);

        if (!outputOk)
        {
            SetStatus($"Output error: {inputError}", StatusKind.Error);
            return;
        }

        _isConnected                 = true;
        _outPortName                 = outName ?? "";
        ConnectButton.Content        = "Disconnect";
        _initPatchButton!.IsEnabled  = true;
        PanicButton.IsEnabled        = true;
        UpdatePatchGridAvailability();
        UpdateSyncIndicator(_patch.UnsyncedCount);
        // Re-scan in case the user added/edited PRM files while disconnected.
        _prm.RescanFolder();
        if (_prm.PatternSync)
            GoToMirrorInitialPatch();

        if (inputError != null)
            SetStatus($"→ {outName}  (input unavailable: {inputError})", StatusKind.Warn);
        else if (inName != null)
            SetStatus($"↔ {outName}  |  listening on {inName}", StatusKind.Ok);
        else
            SetStatus($"→ {outName}  (no input selected)", StatusKind.Ok);
    }

    private async void TryAutoConnect()
    {
        // PopulateDeviceLists already pre-selects an S-1 device when present. If
        // none is found, don't try to connect — the dropdown defaulted to whatever
        // first output the OS reported, which probably isn't the synth.
        int outIdx = _midiMgr.FindOutputIndex(n => n.Contains("S-1", StringComparison.OrdinalIgnoreCase));
        if (outIdx < 0)
        {
            SetStatus("Auto-connect: S-1 output not found.", StatusKind.Warn);
            return;
        }

        await PerformConnectAsync();
    }

    private async void OnInitPatchClicked(object? sender, RoutedEventArgs e)
    {
        _initPatchButton!.IsEnabled = false;
        SetStatus("Initializing…", StatusKind.Info);
        _suppressUndoTracking = true;
        try { _prm.ApplyInitPatch(); }
        finally { _suppressUndoTracking = false; }
        ClearUndoHistory();
        await _patch.SendAllAsync();
        _patch.MarkAllSynced();
        _initPatchButton!.IsEnabled = true;
        SetStatus("Patch initialized.", StatusKind.Ok);
    }

    private void OnPanicClicked(object? sender, RoutedEventArgs e)
    {
        _patch.SendPanic();
        SetStatus("All Sound Off / All Notes Off sent.", StatusKind.Warn);
    }

    // ── Status surfaces ───────────────────────────────────────────────────────

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text       = message;
        StatusText.Foreground = Palette.ForStatus(kind);
    }

    private void UpdateFooterChannels()
    {
        FooterMidiChText.Text = $"CH {MidiChannel}";
        FooterPcChText.Text   = $"PC CH {PcChannel}";
    }

    private void UpdateSyncIndicator(int unsyncedCount)
    {
        // Header chip: only visible when connected AND something is unsynced.
        // "Synced" is the implicit/clean state — no chip is the signal.
        if (_isConnected && unsyncedCount > 0)
        {
            UnsyncedChip.IsVisible  = true;
            UnsyncedChipText.Text   = $"{unsyncedCount} UNSYNCED";
        }
        else
        {
            UnsyncedChip.IsVisible = false;
        }

        // Footer dot + label carry connection state. The Connect/Disconnect button
        // expresses state via its label rather than tint.
        if (_isConnected)
        {
            FooterConnDot.Background  = Palette.StatusOk;
            FooterConnText.Foreground = Palette.StatusOk;
            FooterConnText.Text = string.IsNullOrEmpty(_outPortName)
                ? "MIDI · Connected"
                : $"MIDI · {_outPortName}";
        }
        else
        {
            FooterConnDot.Background  = s_footerDotOff;
            FooterConnText.Foreground = Palette.FgLabel;
            FooterConnText.Text = "Not connected";
        }
    }

    private void GoToMirrorInitialPatch()
    {
        int program = Math.Clamp(_mirrorInitialProgram, 0, 63);
        _patch.SendProgramChange(program, PcChannel);
        HighlightPatchButton(program);
        if ((uint)program < (uint)_patchButtons.Count) _patchButtons[program].Focus();
    }

    // ── Mod / activity timer ──────────────────────────────────────────────────

    private void OnModTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastModTick).TotalSeconds, 0.1);
        _lastModTick = now;

        bool midiLit = (now - _lastMidiActivity).TotalMilliseconds < 150;
        if (midiLit != _midiDotLit)
        {
            _midiDotLit = midiLit;
            MidiActivityDot.Background = midiLit ? MidiDotActive : MidiDotIdle;
            FooterActivityText.Text       = midiLit ? "active" : "idle";
            FooterActivityText.Foreground = midiLit ? Palette.StatusWarn : Palette.FgMute;
        }

        if (_viewModel.Tick(dt))
        {
            _filterCurveUpdate?.Invoke();
            _envelopeDotUpdate?.Invoke();
            _oscWaveformUpdate?.Invoke();
        }
    }
}
