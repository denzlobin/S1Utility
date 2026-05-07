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

    private static readonly IBrush MidiDotActive = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush MidiDotIdle   = new SolidColorBrush(Color.Parse("#2C2C3A"));

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
}
