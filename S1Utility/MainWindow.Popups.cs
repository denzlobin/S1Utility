using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace S1Utility;

// All popup-style windows: the Settings popup, the About / Support pair, and the
// three modal warning dialogs (Reset preferences, non-S-1 Connect, Patch Mirror
// safety warning).
public partial class MainWindow
{
    // ── Settings popup ────────────────────────────────────────────────────────
    //
    // Hosts the "set once" config: MIDI channel, Program Change channel, PRM folder,
    // Patch Mirror initial patch. The controls are field-owned by MainWindow so they
    // keep their state, items, and event-handler wiring across opens — we just
    // re-parent them into the popup each time and detach on close so the next open
    // can re-add them.

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
        MirrorInitialBankCombo.HorizontalAlignment    = HorizontalAlignment.Stretch;
        MirrorInitialPatternCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        PrmFolderBox.HorizontalAlignment              = HorizontalAlignment.Stretch;

        var grid = new Grid
        {
            Margin            = new Thickness(22, 18, 22, 18),
            RowDefinitions    = RowDefinitions.Parse("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
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

        Place(RowLabel("Startup"),                    3, 0);
        Place(AutoConnectCheckBox,                     3, 1, 2);

        var sep = new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#2C2C36")),
            Margin     = new Thickness(0, 10, 0, 8),
        };
        Place(sep, 4, 0, 3);

        Place(SectionHeader("PRM FILES"), 5, 0, 3);

        Place(RowLabel("PRM Folder"),     6, 0);
        Place(PrmFolderBox,                6, 1);
        Place(BrowsePrmFolderButton,       6, 2);

        var prmHint = new TextBlock
        {
            Text       = "Folder of .PRM patch backups exported from the S-1. Used by Patch Mirror and the Patch Inspector. " +
                         "To export: connect USB, then hold PLAY while powering on. Files appear in the BACKUP folder.",
            Classes    = { "body" },
            Foreground = new SolidColorBrush(Color.Parse("#7878A0")),
            Margin     = new Thickness(0, 2, 0, 0),
        };
        Grid.SetRow(prmHint, 7);
        Grid.SetColumn(prmHint, 1);
        Grid.SetColumnSpan(prmHint, 2);
        grid.Children.Add(prmHint);

        var mirrorSep = new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#2C2C36")),
            Margin     = new Thickness(0, 10, 0, 8),
        };
        Place(mirrorSep, 8, 0, 3);

        Place(SectionHeader("PATCH MIRROR"),  9, 0, 3);

        var mirrorPatchRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            ColumnSpacing     = 8,
        };
        Grid.SetColumn(MirrorInitialBankCombo,    0); mirrorPatchRow.Children.Add(MirrorInitialBankCombo);
        Grid.SetColumn(MirrorInitialPatternCombo, 1); mirrorPatchRow.Children.Add(MirrorInitialPatternCombo);

        const string mirrorPatchTooltip =
            "With Patch Mirror enabled, this program is triggered on every connection.";
        var mirrorPatchLabel = RowLabel("Initial Patch");
        ToolTip.SetTip(mirrorPatchLabel,           mirrorPatchTooltip);
        ToolTip.SetTip(MirrorInitialBankCombo,     mirrorPatchTooltip);
        ToolTip.SetTip(MirrorInitialPatternCombo,  mirrorPatchTooltip);
        Place(mirrorPatchLabel, 10, 0);
        Place(mirrorPatchRow,   10, 1, 2);

        const string forcePushTooltip =
            "After every Program Change (whether you click a pattern button, navigate with " +
            "arrow keys, or the synth sends PC), push the editor's CC values to the device.\n\n" +
            "Only CC-mapped parameters are sent. PRM-only data (sequencer steps, draw wave, " +
            "chop pattern, D-Motion assigns, advanced FX, riser) has no MIDI path and stays " +
            "whatever the device loaded.";
        var forcePushLabel = RowLabel("Force CCs on PC");
        ToolTip.SetTip(forcePushLabel,           forcePushTooltip);
        ToolTip.SetTip(MirrorForcePushCheckBox,  forcePushTooltip);
        Place(forcePushLabel,           11, 0);
        Place(MirrorForcePushCheckBox,  11, 1, 2);

        var resetBtn = new Button
        {
            Content             = "Reset preferences",
            Classes             = { "toolbar" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(0, 18, 0, 0),
        };
        ToolTip.SetTip(resetBtn, "Wipe all stored preferences and revert to first-launch defaults.");
        Grid.SetRow(resetBtn, 12);
        Grid.SetColumn(resetBtn, 0);
        Grid.SetColumnSpan(resetBtn, 3);
        grid.Children.Add(resetBtn);

        var closeBtn = new Button
        {
            Content             = "Close",
            Classes             = { "toolbar" },
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth            = 72,
            Margin              = new Thickness(0, 18, 0, 0),
        };
        Grid.SetRow(closeBtn, 12);
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

        resetBtn.Click += async (_, _) => await ResetPreferencesAsync(window);
        closeBtn.Click += (_, _) => window.Close();

        window.Closed += (_, _) =>
        {
            // Detach so the controls can be re-parented into the next popup.
            (ChannelCombo.Parent              as Panel)?.Children.Remove(ChannelCombo);
            (ProgramChangeChannelCombo.Parent as Panel)?.Children.Remove(ProgramChangeChannelCombo);
            (MirrorInitialBankCombo.Parent    as Panel)?.Children.Remove(MirrorInitialBankCombo);
            (MirrorInitialPatternCombo.Parent as Panel)?.Children.Remove(MirrorInitialPatternCombo);
            (MirrorForcePushCheckBox.Parent   as Panel)?.Children.Remove(MirrorForcePushCheckBox);
            (AutoConnectCheckBox.Parent       as Panel)?.Children.Remove(AutoConnectCheckBox);
            (PrmFolderBox.Parent              as Panel)?.Children.Remove(PrmFolderBox);
            (BrowsePrmFolderButton.Parent     as Panel)?.Children.Remove(BrowsePrmFolderButton);
            _settingsWindow = null;
        };

        _settingsWindow = window;
        window.Show(this);
    }

    private async Task ResetPreferencesAsync(Window settingsOwner)
    {
        bool ok = await ShowResetPreferencesWarningAsync(settingsOwner);
        if (!ok) return;

        // Persist defaults to disk and reload backing fields.
        SettingsStore.Save(SettingsPath, new SettingsV1());
        LoadSettings();

        // Push the reset values into every UI surface that mirrors them.
        ChannelCombo.SelectedIndex              = _midiChannel - 1;
        ProgramChangeChannelCombo.SelectedIndex = _pcChannel - 1;
        MirrorInitialBankCombo.SelectedIndex    = _mirrorInitialProgram / 16;
        MirrorInitialPatternCombo.SelectedIndex = _mirrorInitialProgram % 16;
        MirrorForcePushCheckBox.IsChecked       = _mirrorForcePushOnPc;
        AutoConnectCheckBox.IsChecked           = _autoConnect;
        PrmFolderBox.Text                       = _prm.PrmFolder;
        UpdateFooterChannels();

        // Reset live-features toggles: PatternSync just went false, FilterModEnabled
        // just went false. RefreshLiveFeaturesState handles enabled state; we still
        // need the explicit SetActive for Animations because the helper only flips
        // it inside the auto-disable branch.
        _animationsToggle?.SetActive(_viewModel.FilterModEnabled);
        RefreshLiveFeaturesState();

        // PRM folder cleared → rescan invalidates the in-memory program table,
        // PatchAvailabilityChanged refreshes the patch grid, banner updates.
        _prm.RescanFolder();
        UpdateInspectorBanner();
        UpdatePatchGridAvailability();

        // ADSR warning glyph depends on FilterModEnabled — re-evaluate.
        _envelopeWarningUpdate?.Invoke();
        _filterCurveUpdate?.Invoke();
        _envelopeDotUpdate?.Invoke();

        SetStatus("Preferences reset to defaults.", StatusKind.Info);
    }

    // ── Warning dialogs ───────────────────────────────────────────────────────

    private async Task<bool> ShowResetPreferencesWarningAsync(Window owner)
    {
        bool confirmed = false;

        var yesBtn = new Button { Content = "Reset preferences", Classes = { "toolbar" } };
        var noBtn  = new Button { Content = "Cancel",            Classes = { "toolbar" } };

        var dlg = new Window
        {
            Title                 = "Reset preferences?",
            Width                 = 460,
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
                        Text       = "⚠  THIS WILL OVERWRITE YOUR SETTINGS",
                        FontSize   = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = Palette.StatusWarn,
                    },
                    new TextBlock
                    {
                        Classes    = { "body" },
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                        Text       =
                            "MIDI Channel, Program Change Channel, auto-connect, the PRM folder " +
                            "path, Patch Mirror state, and the Patch Mirror dialog opt-out will all " +
                            "be reset to their first-launch defaults. This cannot be undone.\n\n" +
                            "The current MIDI connection is left alone — the new MIDI channel " +
                            "takes effect on the next Connect.",
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

        await dlg.ShowDialog(owner);
        return confirmed;
    }

    private async Task<bool> ShowNonS1ConnectWarningAsync(string deviceName)
    {
        bool confirmed = false;

        var yesBtn = new Button { Content = "Connect anyway", Classes = { "toolbar" } };
        var noBtn  = new Button { Content = "Cancel",         Classes = { "toolbar" } };

        var dlg = new Window
        {
            Title                 = "Connect to non-S-1 device?",
            Width                 = 460,
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
                        Text       = "⚠  NON-S-1 DEVICE SELECTED",
                        FontSize   = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = Palette.StatusWarn,
                    },
                    new TextBlock
                    {
                        Classes    = { "body" },
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                        Text       =
                            $"The selected output \"{deviceName}\" doesn't look like an S-1.\n\n" +
                            "If you continue, the editor will send CC traffic to that device. " +
                            "If the S-1 is just powered off, cancel and turn it on, then click Connect again.",
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
                        Foreground = Palette.StatusWarn,
                    },
                    new TextBlock
                    {
                        Classes    = { "body" },
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                        Text       =
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
                        Classes    = { "body" },
                        Foreground = new SolidColorBrush(Color.Parse("#FF8844")),
                        FontWeight = FontWeight.SemiBold,
                        Text       =
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

    // ── About / Support ───────────────────────────────────────────────────────

    private const string SupportUrl = "https://ko-fi.com/dzl0";
    private const string RepoUrl    = "https://github.com/denzlobin/S1Utility";

    private void OnSupportClicked(object? sender, RoutedEventArgs e) => OpenUrl(SupportUrl);

    private void OnInfoClicked(object? sender, RoutedEventArgs e)
    {
        if (_infoWindow != null) { _infoWindow.Activate(); return; }

        var infoVer = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = !string.IsNullOrEmpty(infoVer)
            ? "v" + infoVer.Split('+')[0]
            : "";

        var titleText = new TextBlock
        {
            Text                = "S-1 UTILITY",
            FontSize            = 22,
            FontWeight          = FontWeight.Bold,
            LetterSpacing       = 2.5,
            Foreground          = new SolidColorBrush(Color.Parse("#E0E0E8")),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var versionText = new TextBlock
        {
            Text                = version,
            FontSize            = 11,
            FontFamily          = new FontFamily("Cascadia Mono,Consolas,monospace"),
            Foreground          = new SolidColorBrush(Color.Parse("#7878A0")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 2, 0, 0),
        };

        var rule = new Border
        {
            Height              = 1,
            Width               = 120,
            Background          = new SolidColorBrush(Color.Parse("#3A3A46")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 16, 0, 16),
        };

        var authorText = new TextBlock
        {
            Text                = "by Denis Zlobin",
            FontSize            = 12,
            Foreground          = new SolidColorBrush(Color.Parse("#A0A0B8")),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var repoLink = MakeLink(RepoUrl, "github.com/denzlobin/S1Utility");
        var repoLinkHost = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 4, 0, 0),
            Children            = { repoLink },
        };

        var licenseText = new TextBlock
        {
            Text                = "Licensed under GPL-3.0",
            Classes             = { "body" },
            Foreground          = new SolidColorBrush(Color.Parse("#7878A0")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 16, 0, 0),
        };

        var disclaimerText = new TextBlock
        {
            Text                = "Not affiliated with Roland Corporation. \"S-1\" is a trademark of Roland Corporation.",
            Classes             = { "body" },
            Foreground          = new SolidColorBrush(Color.Parse("#666677")),
            TextAlignment       = TextAlignment.Center,
            Margin              = new Thickness(0, 14, 0, 0),
            MaxWidth            = 360,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel
        {
            Margin   = new Thickness(28, 28, 28, 22),
            Children = { titleText, versionText, rule, authorText, repoLinkHost, licenseText, disclaimerText },
        };

        _infoWindow = new Window
        {
            Title                 = "About S-1 Utility",
            Width                 = 440,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            Background            = new SolidColorBrush(Color.Parse("#18181E")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = stack,
        };
        _infoWindow.Closed += (_, _) => _infoWindow = null;
        _infoWindow.Show(this);
    }

    private static TextBlock MakeLink(string url, string label)
    {
        var link = new TextBlock
        {
            Text       = label,
            FontSize   = 11,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#80B0E0")),
            Cursor     = new Cursor(StandardCursorType.Hand),
        };
        link.PointerPressed += (_, _) => OpenUrl(url);
        return link;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Logger.Error($"Failed to open URL: {url}", ex);
        }
    }
}
