using Avalonia.Media;

namespace S1Utility;

// Single source of truth for non-accent UI colours used outside of the OSC /
// filter / ADSR drawing code. Accents (Osc / Filt / Env / Lfo / Voice / Fx /
// Seq / Dm / Warn) stay defined in MainWindow.axaml.cs because they propagate
// into the section-aware widget factories.
//
// Named `Tokens` rather than `Theme` because Avalonia's StyledElement already
// exposes a `Theme` property and the name shadowing breaks field initializers.
internal static class Tokens
{
    // ── Backgrounds ──────────────────────────────────────────────────────────
    public static readonly IBrush BgApp     = new SolidColorBrush(Color.Parse("#0E0E12"));
    public static readonly IBrush BgWindow  = new SolidColorBrush(Color.Parse("#15151A"));
    public static readonly IBrush BgToolbar = new SolidColorBrush(Color.Parse("#181820"));
    public static readonly IBrush BgCard    = new SolidColorBrush(Color.Parse("#161616"));
    public static readonly IBrush BgInset   = new SolidColorBrush(Color.Parse("#0E0E12"));
    public static readonly IBrush BgChip    = new SolidColorBrush(Color.Parse("#1F1F26"));
    public static readonly IBrush BgChipHov = new SolidColorBrush(Color.Parse("#23232C"));
    public static readonly IBrush BgInput   = new SolidColorBrush(Color.Parse("#1A1A24"));

    // ── Borders ──────────────────────────────────────────────────────────────
    public static readonly IBrush BdCard    = new SolidColorBrush(Color.Parse("#232323"));
    public static readonly IBrush BdToolbar = new SolidColorBrush(Color.Parse("#2C2C36"));
    public static readonly IBrush BdInset   = new SolidColorBrush(Color.Parse("#1E1E26"));
    public static readonly IBrush BdChip    = new SolidColorBrush(Color.Parse("#333344"));
    public static readonly IBrush BdRow     = new SolidColorBrush(Color.Parse("#1D1D1D"));

    // ── Text ─────────────────────────────────────────────────────────────────
    public static readonly IBrush FgHi    = new SolidColorBrush(Color.Parse("#CCCCDD"));
    public static readonly IBrush FgVal   = new SolidColorBrush(Color.Parse("#A0A0B5"));
    public static readonly IBrush FgLabel = new SolidColorBrush(Color.Parse("#7878A0"));
    public static readonly IBrush FgMute  = new SolidColorBrush(Color.Parse("#666677"));
    public static readonly IBrush FgDim   = new SolidColorBrush(Color.Parse("#3A3A48"));
    public static readonly IBrush FgOff   = new SolidColorBrush(Color.Parse("#4A4A4A"));

    public static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,monospace");
}
