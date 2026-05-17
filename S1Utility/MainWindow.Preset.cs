using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using S1Utility.Core;

namespace S1Utility;

// Preset save/load (.s1patch), PRM folder picker, PRM-file Open dialog (Inspect Only
// vs Load Into Editor), and the inspector-banner state machine that depends on
// PRM availability. PRM-meta event handler also lives here since it fires when a
// file's data has been loaded into the inspector.
public partial class MainWindow
{
    private static readonly FilePickerFileType S1PatchFileType =
        new("S-1 Patch") { Patterns = new[] { "*.s1patch" } };

    private static readonly FilePickerFileType PrmFileType =
        new("S-1 PRM Patch") { Patterns = new[] { "*.PRM", "*.prm" } };

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    // ── Preset save / load ────────────────────────────────────────────────────

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Preset",
            SuggestedFileName = "preset",
            DefaultExtension  = "s1patch",
            FileTypeChoices   = new[] { S1PatchFileType },
        });

        if (file is null) return;

        // The preset's own Name field is just the chosen file's stem — no
        // separate user-editable preset name exists in the UI anymore.
        var presetName = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        var preset = _patch.ToPreset(string.IsNullOrWhiteSpace(presetName) ? "Untitled" : presetName);
        preset.PrmOnly = _prm.AllPrmOnlyParams()
            .Select(p => new PrmOnlyEntry { PrmKey = p.PrmKey, Value = p.Value })
            .ToList();
        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, preset, JsonOptions);

        SetStatus($"Saved: {file.Name}", StatusKind.Ok);
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
            SetStatus($"Load error: {ex.Message}", StatusKind.Error);
            return;
        }

        if (preset is null) return;

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
        SetStatus($"Loaded: {preset.Name}", StatusKind.Ok);
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
        var localPath = files[0].TryGetLocalPath() ?? files[0].Name;
        try
        {
            parsed = await Task.Run(() => PrmFileParser.Parse(localPath));
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"PRM parse failed (manual open): {localPath}", ex);
            SetStatus($"PRM load error: {ex.Message}", StatusKind.Error);
            return;
        }

        var fileName = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
        var choice   = await ShowOpenPrmChoiceAsync(fileName);

        if (choice == OpenPrmChoice.Cancel) return;

        if (choice == OpenPrmChoice.InspectOnly)
        {
            _prm.LoadInspector(parsed);
            _prm.LastLoadedSourceName = fileName;
            SetStatus($"Inspecting: {fileName}", StatusKind.Ok);
            return;
        }

        // LoadIntoEditor: write to live patch + push to synth (if connected).
        _suppressUndoTracking = true;
        try { _prm.ApplyPrmData(parsed); }
        finally { _suppressUndoTracking = false; }
        _prm.LastLoadedSourceName = fileName;
        ClearUndoHistory();
        SetStatus("Sending PRM values…", StatusKind.Info);
        await _patch.SendAllAsync();
        SetStatus($"Loaded PRM: {fileName}", StatusKind.Ok);
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
                Margin   = new Avalonia.Thickness(24, 20),
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
                        Foreground   = Palette.StatusOk,
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
                        Foreground   = Palette.StatusWarn,
                        Margin       = new Avalonia.Thickness(0, 6, 0, 0),
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
                        Margin              = new Avalonia.Thickness(0, 8, 0, 0),
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

    // ── Inspector state (depends on PRM availability) ─────────────────────────

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
            InspectorBannerHeadline.Text  = "NO PATCH DATA TO INSPECT";
            InspectorBannerPrimary.Text   = "Back up your patches from the synth, then point the PRM folder below at the resulting files.";
            InspectorBannerSecondary.Text = "The Patch Inspector reads .PRM files saved by the S-1. With a folder configured, this tab shows every parameter in the selected pattern, including the sequencer steps, draw wave, chop pattern, and D-Motion assigns that have no MIDI equivalent.";
            InspectorBanner.IsVisible     = true;
            PrmViewerGrid.IsVisible       = false;
        }
        else if (slotBroken)
        {
            (int bank, int pat) = SlotLabel(slot);
            InspectorBannerHeadline.Text  = "PRM FILE IS MALFORMED";
            InspectorBannerPrimary.Text   = $"Bank {bank}, Pattern {pat} contains unknown keys or failed to parse. The data on disk is not reliable enough to display.";
            InspectorBannerSecondary.Text = "This usually means the file was edited by hand or saved by another tool. Other slots in the folder are unaffected.";
            InspectorBanner.IsVisible     = true;
            PrmViewerGrid.IsVisible       = false;
        }
        else if (slotMissing)
        {
            (int bank, int pat) = SlotLabel(slot);
            InspectorBannerHeadline.Text  = "NO PRM FILE FOR THIS SLOT";
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
}
