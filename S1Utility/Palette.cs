using System;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;

namespace S1Utility;

// Lookup helper for shared UI brushes. The hex values live in App.axaml's
// Application.Resources; Palette resolves each key once and caches the IBrush.
//
// Strict: missing keys throw, so XAML stays the single source of truth — there is
// no hardcoded fallback hex in this file.
//
// Named Palette (not Theme) because Avalonia's StyledElement already exposes a
// `Theme` instance property; using the same name from inside a Window would bind
// to the inherited member.
internal static class Palette
{
    private static readonly ConcurrentDictionary<string, IBrush> _cache = new();

    private static IBrush Get(string key) => _cache.GetOrAdd(key, ResolveOrThrow);

    private static IBrush ResolveOrThrow(string key)
    {
        if (Application.Current is { Resources: var resources }
            && resources.TryGetResource(key, null, out var obj)
            && obj is IBrush brush)
        {
            return brush;
        }
        throw new InvalidOperationException(
            $"Palette resource '{key}' not found in Application.Current.Resources — " +
            "check App.axaml Application.Resources.");
    }

    // ── Section accents ──────────────────────────────────────────────────────
    public static IBrush AccentOsc    => Get("AccentOsc");
    public static IBrush AccentSeq    => Get("AccentSeq");
    public static IBrush AccentFilter => Get("AccentFilter");
    public static IBrush AccentEnv    => Get("AccentEnv");
    public static IBrush AccentLfo    => Get("AccentLfo");
    public static IBrush AccentVoice  => Get("AccentVoice");
    public static IBrush AccentFx     => Get("AccentFx");
    public static IBrush AccentDm     => Get("AccentDm");
    public static IBrush AccentDmAlt  => Get("AccentDmAlt");
    public static IBrush AccentWarn   => Get("AccentWarn");

    // ── Surface backgrounds ──────────────────────────────────────────────────
    public static IBrush BgApp     => Get("BgApp");
    public static IBrush BgWindow  => Get("BgWindow");
    public static IBrush BgToolbar => Get("BgToolbar");
    public static IBrush BgCard    => Get("BgCard");
    public static IBrush BgChip    => Get("BgChip");
    public static IBrush BgChipHov => Get("BgChipHov");
    public static IBrush BgInput   => Get("BgInput");
    // Inset surfaces (visualizer backgrounds, LED off states) share BgApp's hex.
    public static IBrush BgInset   => Get("BgApp");

    // ── Borders ──────────────────────────────────────────────────────────────
    public static IBrush BdCard    => Get("BdCard");
    public static IBrush BdToolbar => Get("BdToolbar");
    public static IBrush BdInset   => Get("BdInset");
    public static IBrush BdChip    => Get("BdChip");
    public static IBrush BdRow     => Get("BdRow");

    // ── Text ─────────────────────────────────────────────────────────────────
    public static IBrush FgHi    => Get("FgHi");
    public static IBrush FgVal   => Get("FgVal");
    public static IBrush FgLabel => Get("FgLabel");
    public static IBrush FgMute  => Get("FgMute");
    public static IBrush FgDim   => Get("FgDim");
    public static IBrush FgOff   => Get("FgOff");

    // Monospace font stack — used wherever numeric values are rendered.
    public static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,monospace");
}
