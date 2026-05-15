using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using S1Utility.Core;

namespace S1Utility;

public partial class MainWindow
{
    private static readonly string SettingsPath = ResolveSettingsPath();

    private static string ResolveSettingsPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "S1Utility");
        try { System.IO.Directory.CreateDirectory(dir); }
        catch { /* fall through; WriteAllText will surface the error */ }
        return System.IO.Path.Combine(dir, "s1editor.settings.json");
    }

    private static IBrush MidiDotActive => Palette.AccentOsc;
    private static readonly IBrush MidiDotIdle = new SolidColorBrush(Color.Parse("#2C2C3A"));

    private static readonly FilePickerFileType S1PatchFileType =
        new("S-1 Patch") { Patterns = new[] { "*.s1patch" } };

    private static readonly FilePickerFileType PrmFileType =
        new("S-1 PRM Patch") { Patterns = new[] { "*.PRM", "*.prm" } };

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

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

        DeviceCombo.DropDownOpened += (_, _) => ReenumerateDevices();
        InputCombo.DropDownOpened  += (_, _) => ReenumerateDevices();

        if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
        if (InputCombo.Items.Count  > 0) InputCombo.SelectedIndex  = 0;
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
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private async void OnConnectClicked(object? sender, RoutedEventArgs e) => await PerformConnectAsync();

    private void OnDeviceDisconnected()
    {
        _isConnected                 = false;
        _outPortName                 = "";
        _initPatchButton!.IsEnabled      = false;
        PanicButton.IsEnabled        = false;
        ConnectButton.Content        = "Reconnect";
        SetStatus("Device disconnected.", "#FF6B6B");
        // Null the transport so further knob drags don't throw inside the stale
        // ManagedMidiTransport and get silently swallowed.
        _patch.Disconnect();
        // Without the synth we no longer know any parameter's state — gray every
        // knob and show "?" in value labels. Honest about what it knows.
        _patch.ResetAllSync();
        UpdateSyncIndicator(_patch.UnsyncedCount);
        ClearDirtyTracking();
        UpdatePatchGridAvailability();
    }

    private void UpdatePatchGridAvailability()
    {
        // Inspector tab is browse-only and works without the device, but only
        // if there is actually PRM data on disk to browse. The editor tab gates
        // the grid on connection so clicks always correspond to hardware action.
        bool onInspector  = MainTabs.SelectedIndex == 1;
        bool inspectorOk  = onInspector && PrmFolderHasValidFiles();
        PatchGridContainer.IsEnabled = _isConnected || inspectorOk;
    }

    private void UpdateInspectorBanner()
    {
        // Three blocking states, in priority order:
        //   1. No folder configured or folder has no valid PRMs at all
        //   2. Current slot's file is malformed (parse failure or unknown keys)
        //   3. Current slot has no PRM file in the folder
        bool hasFolder    = PrmFolderHasValidFiles();
        int  slot         = _currentSlotIndex;
        bool slotMissing  = hasFolder && slot >= 0
                            && !_prm.AvailablePrograms.Contains(slot)
                            && !_prm.MalformedPrograms.Contains(slot);
        bool slotBroken   = hasFolder && slot >= 0
                            && _prm.MalformedPrograms.Contains(slot);

        if (!hasFolder)
        {
            InspectorBannerHeadline.Text  = "No patch data to inspect";
            InspectorBannerPrimary.Text   = "Back up your patches from the synth, then point the PRM folder below at the resulting files.";
            InspectorBannerSecondary.Text = "The Patch Inspector reads .PRM files saved by the S-1. With a folder configured, this tab shows every parameter in the selected pattern, including the sequencer steps, draw wave, chop pattern, and D-Motion assigns that have no MIDI equivalent.";
            InspectorBanner.IsVisible     = true;
            PrmViewerGrid.IsVisible       = false;
        }
        else if (slotBroken)
        {
            (int bank, int pat) = SlotLabel(slot);
            InspectorBannerHeadline.Text  = "PRM file is malformed";
            InspectorBannerPrimary.Text   = $"Bank {bank}, Pattern {pat} contains unknown keys or failed to parse. The data on disk is not reliable enough to display.";
            InspectorBannerSecondary.Text = "This usually means the file was edited by hand or saved by another tool. Other slots in the folder are unaffected.";
            InspectorBanner.IsVisible     = true;
            PrmViewerGrid.IsVisible       = false;
        }
        else if (slotMissing)
        {
            (int bank, int pat) = SlotLabel(slot);
            InspectorBannerHeadline.Text  = "No PRM file for this slot";
            InspectorBannerPrimary.Text   = $"Bank {bank}, Pattern {pat} has no backup file in the PRM folder. Selecting it still sends Program Change to the synth, but there is no patch data to inspect.";
            InspectorBannerSecondary.Text = "Back up this pattern from the synth to add it to the folder.";
            InspectorBanner.IsVisible     = true;
            PrmViewerGrid.IsVisible       = false;
        }
        else
        {
            InspectorBanner.IsVisible = false;
            PrmViewerGrid.IsVisible   = true;
        }
    }

    private static (int bank, int pat) SlotLabel(int program) =>
        (program / 16 + 1, program % 16 + 1);

    private async Task PerformConnectAsync()
    {
        // Re-enumerate first so unplug/replug recovers without a separate refresh step.
        ReenumerateDevices();

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
        _outPortName                 = outName ?? "";
        ConnectButton.Content        = "Reconnect";
        _initPatchButton!.IsEnabled      = true;
        PanicButton.IsEnabled        = true;
        UpdatePatchGridAvailability();
        UpdateSyncIndicator(_patch.UnsyncedCount);
        // Re-scan in case the user added/edited PRM files while disconnected.
        _prm.RescanFolder();
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
            if (doc.RootElement.TryGetProperty("skipPatternSyncWarning", out var skipWarnEl)) _skipPatternSyncWarning     = skipWarnEl.GetBoolean();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load failed: {ex.Message}");
            SetStatus("Settings failed to load. Using defaults.", "#F0A040");
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
                skipPatternSyncWarning = _skipPatternSyncWarning,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            SetStatus("Settings could not be saved.", "#F0A040");
        }
    }

    private async void OnInitPatchClicked(object? sender, RoutedEventArgs e)
    {
        _initPatchButton!.IsEnabled = false;
        SetStatus("Initializing…", "#AAAAAA");
        _suppressUndoTracking = true;
        try { _prm.ApplyInitPatch(); }
        finally { _suppressUndoTracking = false; }
        ClearUndoHistory();
        await _patch.SendAllAsync();
        _patch.MarkAllSynced();
        _initPatchButton!.IsEnabled = true;
        SetStatus("Patch initialized.", "#70C870");
    }

    private void OnPanicClicked(object? sender, RoutedEventArgs e)
    {
        _patch.SendPanic();
        SetStatus("All Sound Off / All Notes Off sent.", "#F0A040");
    }

    // ── Preset save / load ────────────────────────────────────────────────────

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
        _suppressUndoTracking = true;
        try
        {
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
        }
        finally { _suppressUndoTracking = false; }
        ClearUndoHistory();

        await _patch.SendAllAsync();
        SetStatus($"Loaded: {preset.Name}", "#70C870");
    }

    private async Task<bool> ShowPatternSyncWarningAsync()
    {
        bool confirmed = false;

        var yesBtn = new Button { Content = "Enable Patch Mirror", Classes = { "toolbar" } };
        var noBtn  = new Button { Content = "Cancel",             Classes = { "toolbar" } };

        var dontAskAgain = new CheckBox
        {
            Content    = "Don't ask me again",
            IsChecked  = false,
            FontSize   = 11,
            Foreground = new SolidColorBrush(Color.Parse("#A0A0B8")),
        };

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
                    dontAskAgain,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing     = 8,
                        Children    = { yesBtn, noBtn },
                    },
                },
            },
        };

        yesBtn.Click += (_, _) =>
        {
            confirmed = true;
            if (dontAskAgain.IsChecked == true)
            {
                _skipPatternSyncWarning = true;
                SaveSettings();
            }
            dlg.Close();
        };
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
        UpdateInspectorBanner();
        UpdatePatchGridAvailability();
        _prm.RescanFolder();
        SaveSettings();
    }

    // ── PRM file open ─────────────────────────────────────────────────────────

    private enum OpenPrmChoice { Cancel, InspectOnly, LoadIntoEditor }

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

        var fileName = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
        var choice   = await ShowOpenPrmChoiceAsync(fileName);

        if (choice == OpenPrmChoice.Cancel) return;

        if (choice == OpenPrmChoice.InspectOnly)
        {
            _prm.LoadInspector(parsed);
            SetStatus($"Inspecting: {fileName}", "#70C870");
            return;
        }

        // LoadIntoEditor: write to live patch + push to synth (if connected).
        _suppressUndoTracking = true;
        try { _prm.ApplyPrmData(parsed); }
        finally { _suppressUndoTracking = false; }
        ClearUndoHistory();
        PresetNameBox.Text = fileName;
        SetStatus("Sending PRM values…", "#AAAAAA");
        await _patch.SendAllAsync();
        SetStatus($"Loaded PRM: {fileName}", "#70C870");
    }

    private async Task<OpenPrmChoice> ShowOpenPrmChoiceAsync(string fileName)
    {
        var result = OpenPrmChoice.Cancel;

        var inspectBtn = new Button { Content = "Inspect Only",     Classes = { "toolbar" } };
        var loadBtn    = new Button { Content = "Load Into Editor", Classes = { "toolbar" } };
        var cancelBtn  = new Button { Content = "Cancel",           Classes = { "toolbar" } };

        var dlg = new Window
        {
            Title                 = "Open PRM File",
            Width                 = 540,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = new SolidColorBrush(Color.Parse("#1C1C1C")),
            Content = new StackPanel
            {
                Margin   = new Thickness(24, 20),
                Spacing  = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text       = fileName,
                        FontSize   = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#E0E0E8")),
                    },
                    new TextBlock
                    {
                        FontSize     = 11,
                        Foreground   = new SolidColorBrush(Color.Parse("#A0A0B8")),
                        TextWrapping = TextWrapping.Wrap,
                        Text         = "Choose how to open this file.",
                    },
                    new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#333344")) },

                    new TextBlock
                    {
                        FontSize     = 11,
                        FontWeight   = FontWeight.SemiBold,
                        Foreground   = new SolidColorBrush(Color.Parse("#70C870")),
                        Text         = "Inspect Only",
                    },
                    new TextBlock
                    {
                        FontSize     = 10.5,
                        Foreground   = new SolidColorBrush(Color.Parse("#BBBBCC")),
                        TextWrapping = TextWrapping.Wrap,
                        Text         = "Populates the Patch Inspector tab with this file's data. " +
                                       "Editor and synth are unchanged. Use this to study other patches " +
                                       "without overwriting your current work.",
                    },

                    new TextBlock
                    {
                        FontSize     = 11,
                        FontWeight   = FontWeight.SemiBold,
                        Foreground   = new SolidColorBrush(Color.Parse("#F0A040")),
                        Margin       = new Thickness(0, 6, 0, 0),
                        Text         = "Load Into Editor",
                    },
                    new TextBlock
                    {
                        FontSize     = 10.5,
                        Foreground   = new SolidColorBrush(Color.Parse("#BBBBCC")),
                        TextWrapping = TextWrapping.Wrap,
                        Text         = "Sends the file's CC-mapped values to the editor (and to the synth, if connected). " +
                                       "Covers Oscillator, Filter, Envelope, LFO, Voice, and Effect levels: " +
                                       "around 50 parameters with MIDI equivalents.\n\n" +
                                       "PRM-only data (sequencer steps, chop pattern, draw waveform, riser, " +
                                       "D-Motion, advanced FX) has no MIDI equivalent and stays in the Inspector tab only.",
                    },

                    new StackPanel
                    {
                        Orientation         = Orientation.Horizontal,
                        Spacing             = 8,
                        Margin              = new Thickness(0, 8, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children            = { cancelBtn, inspectBtn, loadBtn },
                    },
                },
            },
        };

        inspectBtn.Click += (_, _) => { result = OpenPrmChoice.InspectOnly;    dlg.Close(); };
        loadBtn.Click    += (_, _) => { result = OpenPrmChoice.LoadIntoEditor; dlg.Close(); };
        cancelBtn.Click  += (_, _) => dlg.Close();

        await dlg.ShowDialog(this);
        return result;
    }

    private void OnPrmMetaLoaded(object? sender, PrmMetaArgs e)
    {
        _tempoLabel.Text = e.Tempo;
        for (int i = 0; i < 8; i++)
            _motionCcLabels[i].Text = e.MotionCcLabels[i];
    }

    private void SetStatus(string message, string hexColour)
    {
        StatusText.Text       = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(hexColour));
    }

    private static readonly IBrush s_footerDotOff = new SolidColorBrush(Color.Parse("#2A2A33"));
    private static readonly IBrush s_footerDotOn  = new SolidColorBrush(Color.Parse("#70C870"));
    private static readonly IBrush s_footerTextConnected    = new SolidColorBrush(Color.Parse("#70C870"));
    private static readonly IBrush s_footerTextDisconnected = new SolidColorBrush(Color.Parse("#7878A0"));
    private static readonly IBrush s_unsyncedAmber          = new SolidColorBrush(Color.Parse("#F0A040"));

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

        // Footer dot + label carry connection state.
        if (_isConnected)
        {
            FooterConnDot.Background = s_footerDotOn;
            FooterConnText.Foreground = s_footerTextConnected;
            FooterConnText.Text = string.IsNullOrEmpty(_outPortName)
                ? "MIDI · Connected"
                : $"MIDI · {_outPortName}";
            ConnectButton.Classes.Set("connected", true);
        }
        else
        {
            FooterConnDot.Background = s_footerDotOff;
            FooterConnText.Foreground = s_footerTextDisconnected;
            FooterConnText.Text = "Not connected";
            ConnectButton.Classes.Set("connected", false);
        }
    }

    private void GoToPattern1()
    {
        _patch.SendProgramChange(0, PcChannel);
        HighlightPatchButton(0);
        if (_patchButtons.Count > 0) _patchButtons[0].Focus();
    }

    // ── Filter modulation: 60 fps tick ───────────────────────────────────────

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
            FooterActivityText.Foreground = midiLit ? s_unsyncedAmber : Palette.FgMute;
        }

        if (_viewModel.Tick(dt))
        {
            _filterCurveUpdate?.Invoke();
            _envelopeDotUpdate?.Invoke();
            _oscWaveformUpdate?.Invoke();
        }
    }

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

    // ── Settings popup ────────────────────────────────────────────────────────
    //
    // Hosts the "set once" config: MIDI channel, Program Change channel, PRM folder.
    // The controls are field-owned by MainWindow, so they keep their state, items,
    // and event-handler wiring across opens — we just re-parent them into the popup
    // each time and detach on close so the next open can re-add them.

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        static TextBlock SectionHeader(string text) => new()
        {
            Text          = text,
            FontSize      = 10.5,
            FontWeight    = FontWeight.Bold,
            LetterSpacing = 1.5,
            Foreground    = new SolidColorBrush(Color.Parse("#9090A8")),
            Margin        = new Thickness(0, 0, 0, 8),
        };
        static TextBlock RowLabel(string text) => new()
        {
            Text              = text,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.Parse("#A0A0B8")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Stretch the field controls inside the popup so they fill their cells.
        ChannelCombo.HorizontalAlignment              = HorizontalAlignment.Stretch;
        ProgramChangeChannelCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        PrmFolderBox.HorizontalAlignment              = HorizontalAlignment.Stretch;

        var grid = new Grid
        {
            Margin            = new Thickness(22, 18, 22, 18),
            RowDefinitions    = RowDefinitions.Parse("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = ColumnDefinitions.Parse("180,*,Auto"),
            ColumnSpacing     = 12,
            RowSpacing        = 8,
        };

        void Place(Control c, int row, int col, int colSpan = 1)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            if (colSpan > 1) Grid.SetColumnSpan(c, colSpan);
            grid.Children.Add(c);
        }

        var midiHeader = SectionHeader("MIDI");
        Place(midiHeader, 0, 0, 3);

        Place(RowLabel("MIDI Channel"),               1, 0);
        Place(ChannelCombo,                            1, 1, 2);

        Place(RowLabel("Program Change Channel"),     2, 0);
        Place(ProgramChangeChannelCombo,               2, 1, 2);

        var sep = new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#2C2C36")),
            Margin     = new Thickness(0, 10, 0, 8),
        };
        Place(sep, 3, 0, 3);

        Place(SectionHeader("PRM FILES"), 4, 0, 3);

        Place(RowLabel("PRM Folder"),     5, 0);
        Place(PrmFolderBox,                5, 1);
        Place(BrowsePrmFolderButton,       5, 2);

        var prmHint = new TextBlock
        {
            Text         = "Folder of .PRM patch backups exported from the S-1. Used by Patch Mirror and the Patch Inspector.",
            FontSize     = 10,
            Foreground   = new SolidColorBrush(Color.Parse("#7878A0")),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 0),
        };
        Grid.SetRow(prmHint, 6);
        Grid.SetColumn(prmHint, 1);
        Grid.SetColumnSpan(prmHint, 2);
        grid.Children.Add(prmHint);

        var closeBtn = new Button
        {
            Content             = "Close",
            Classes             = { "toolbar" },
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth            = 72,
            Margin              = new Thickness(0, 18, 0, 0),
        };
        Grid.SetRow(closeBtn, 7);
        Grid.SetColumn(closeBtn, 0);
        Grid.SetColumnSpan(closeBtn, 3);
        grid.Children.Add(closeBtn);

        var window = new Window
        {
            Title                 = "Settings",
            Width                 = 480,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            ShowInTaskbar         = false,
            Background            = new SolidColorBrush(Color.Parse("#18181E")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = grid,
        };

        closeBtn.Click += (_, _) => window.Close();

        window.Closed += (_, _) =>
        {
            // Detach so the controls can be re-parented into the next popup.
            (ChannelCombo.Parent              as Panel)?.Children.Remove(ChannelCombo);
            (ProgramChangeChannelCombo.Parent as Panel)?.Children.Remove(ProgramChangeChannelCombo);
            (PrmFolderBox.Parent              as Panel)?.Children.Remove(PrmFolderBox);
            (BrowsePrmFolderButton.Parent     as Panel)?.Children.Remove(BrowsePrmFolderButton);
            _settingsWindow = null;
        };

        _settingsWindow = window;
        window.Show(this);
    }
}
