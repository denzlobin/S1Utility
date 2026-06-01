using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace S1Utility;

// Linux/X11 counterpart to WindowsAspectRatioPolicy. Where Windows hooks
// WM_SIZING to constrain the drag, X11 has a native mechanism for exactly this:
// the WM_NORMAL_HINTS property (XSizeHints) carries min_aspect/max_aspect, and
// a conforming window manager enforces that ratio during an interactive resize.
// So we set both aspect bounds to the design ratio and let the WM do the lock —
// no per-frame correction, no glitchy snap-back.
//
// We talk to X11 over our own short-lived display connection (XSizeHints is just
// a property on the window keyed by XID, settable from any connection), so we
// don't need Avalonia's internal Display pointer. Existing hints (Avalonia's
// PMinSize etc.) are read first and merged, not clobbered.
//
// Degrades gracefully: if libX11 is missing, the connection fails, or a tiling
// WM ignores PAspect, the call is a harmless no-op and the window just resizes
// freely. The Viewbox (Stretch=Uniform) means even an off-ratio window never
// clips a card — it only letterboxes — and FitToScreen keeps the initial size
// within the monitor work area at any resolution.
[SupportedOSPlatform("linux")]
internal sealed class LinuxAspectRatioPolicy : IWindowSizePolicy
{
    // XSizeHints.flags bits (X11/Xutil.h).
    private const long PMinSize = 1 << 4;
    private const long PAspect  = 1 << 7;

    // XSizeHints for LP64 Linux: `long flags` (8 bytes) then int fields (4 each),
    // including the two {int x, int y} aspect pairs. Layout must match exactly.
    [StructLayout(LayoutKind.Sequential)]
    private struct XSizeHints
    {
        public long flags;
        public int  x, y;
        public int  width, height;
        public int  min_width, min_height;
        public int  max_width, max_height;
        public int  width_inc, height_inc;
        public int  min_aspect_x, min_aspect_y;
        public int  max_aspect_x, max_aspect_y;
        public int  base_width, base_height;
        public int  win_gravity;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XGetWMNormalHints(IntPtr display, IntPtr window, ref XSizeHints hints, out long supplied);

    [DllImport("libX11.so.6")]
    private static extern void XSetWMNormalHints(IntPtr display, IntPtr window, ref XSizeHints hints);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    private readonly int _ratioW;
    private readonly int _ratioH;

    public LinuxAspectRatioPolicy(double aspect)
    {
        // Express the ratio as an integer pair; the design size is the natural one.
        _ratioW = (int)Math.Round(aspect * 1000);
        _ratioH = 1000;
    }

    public void Attach(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;

        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;

            // Read existing hints so we preserve PMinSize (Avalonia sets it from
            // MinWidth/MinHeight) and only add the aspect lock.
            var hints = new XSizeHints();
            XGetWMNormalHints(display, handle.Handle, ref hints, out _);

            hints.flags |= PAspect | PMinSize;
            hints.min_aspect_x = _ratioW;
            hints.min_aspect_y = _ratioH;
            hints.max_aspect_x = _ratioW;
            hints.max_aspect_y = _ratioH;

            XSetWMNormalHints(display, handle.Handle, ref hints);
            XFlush(display);
        }
        catch (DllNotFoundException) { /* no libX11 — leave window freely resizable */ }
        catch (EntryPointNotFoundException) { /* unexpected X11 build — no-op */ }
        finally
        {
            if (display != IntPtr.Zero) XCloseDisplay(display);
        }
    }

    // Hints live on the window; nothing to unwind. The window is closing anyway.
    public void Detach() { }
}
