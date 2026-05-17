using System;

namespace S1Utility;

// Settings persistence — load on startup, save on every user-visible setting
// change, and expose the resolved settings-file path that other partials use.
// All the popup/dialog code that *modifies* settings lives in
// `MainWindow.Popups.cs`; this file is just the disk-IO bookend.
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

    private void LoadSettings()
    {
        var s = SettingsStore.Load(SettingsPath);
        _autoConnect                = s.AutoConnect;
        _viewModel.FilterModEnabled = s.FilterModEnabled;
        _prm.PrmFolder              = s.PrmFolder;
        _prm.PatternSync            = s.PatternSync;
        _midiChannel                = s.MidiChannel;
        _pcChannel                  = s.PcChannel;
        _skipPatternSyncWarning     = s.SkipPatternSyncWarning;
        _mirrorInitialProgram       = Math.Clamp(s.MirrorInitialProgram, 0, 63);
    }

    private void SaveSettings() => SettingsStore.Save(SettingsPath, new SettingsV1
    {
        AutoConnect            = _autoConnect,
        FilterModEnabled       = _viewModel.FilterModEnabled,
        PrmFolder              = _prm.PrmFolder,
        PatternSync            = _prm.PatternSync,
        MidiChannel            = MidiChannel,
        PcChannel              = PcChannel,
        SkipPatternSyncWarning = _skipPatternSyncWarning,
        MirrorInitialProgram   = _mirrorInitialProgram,
    });
}
