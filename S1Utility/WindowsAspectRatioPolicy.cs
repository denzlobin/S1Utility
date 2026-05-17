using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace S1Utility;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAspectRatioPolicy : IWindowSizePolicy
{
    private const int  GWLP_WNDPROC      = -4;
    private const int  GWL_STYLE         = -16;
    private const long WS_MAXIMIZEBOX    = 0x00010000;
    private const uint WM_SIZING         = 0x0214;
    private const int  WMSZ_TOP          = 3, WMSZ_TOPLEFT  = 4, WMSZ_TOPRIGHT = 5;
    private const int  WMSZ_BOTTOM       = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);

    private readonly double _aspectRatio;
    private WndProcDelegate? _wndProc;
    private IntPtr _oldWndProc;
    private IntPtr _oldStyle;
    private IntPtr _hWnd;
    private int    _chromeWidth;
    private int    _chromeHeight;

    public WindowsAspectRatioPolicy(double aspectRatio) => _aspectRatio = aspectRatio;

    public void Attach(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null) return;
        _hWnd = handle.Handle;
        _wndProc = WndProcHook;
        _oldWndProc = SetWindowLongPtrW(_hWnd, GWLP_WNDPROC,
                          Marshal.GetFunctionPointerForDelegate(_wndProc));

        // Strip WS_MAXIMIZEBOX so the window can't be maximized (button, double-click
        // on title bar, Win+Up) while drag-resize remains. Maximize would force the
        // window to monitor aspect, which doesn't match the design aspect — the
        // Viewbox would then letterbox the content. Keep aspect-correct via WM_SIZING
        // (drag resize) only.
        _oldStyle = GetWindowLongPtrW(_hWnd, GWL_STYLE);
        var newStyle = (IntPtr)((long)_oldStyle & ~WS_MAXIMIZEBOX);
        SetWindowLongPtrW(_hWnd, GWL_STYLE, newStyle);

        // Capture the chrome offset (title bar + borders) so WM_SIZING can keep
        // the aspect lock on the *client* rect — i.e. the surface the Viewbox
        // actually renders into. Without this, the title bar height offsets the
        // outer window aspect from the client aspect, and the Viewbox letterboxes.
        if (GetWindowRect(_hWnd, out var winRect) && GetClientRect(_hWnd, out var cliRect))
        {
            _chromeWidth  = (winRect.Right  - winRect.Left) - (cliRect.Right  - cliRect.Left);
            _chromeHeight = (winRect.Bottom - winRect.Top)  - (cliRect.Bottom - cliRect.Top);
        }
    }

    public void Detach()
    {
        if (_hWnd == IntPtr.Zero || _oldWndProc == IntPtr.Zero) return;
        SetWindowLongPtrW(_hWnd, GWLP_WNDPROC, _oldWndProc);
        if (_oldStyle != IntPtr.Zero)
            SetWindowLongPtrW(_hWnd, GWL_STYLE, _oldStyle);
        _hWnd = IntPtr.Zero;
        _oldWndProc = IntPtr.Zero;
        _oldStyle = IntPtr.Zero;
        _wndProc = null;
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SIZING)
        {
            var rect = Marshal.PtrToStructure<Win32Rect>(lParam);
            int edge = (int)wParam;
            int w    = rect.Right  - rect.Left;
            int h    = rect.Bottom - rect.Top;

            // Aspect-lock the *client* rect (what the Viewbox sees), not the outer
            // window rect: subtract chrome on input, add it back on output.
            int clientW = w - _chromeWidth;
            int clientH = h - _chromeHeight;

            // Pure top/bottom drag: lock client height, adjust width rightward.
            // All other edges (including corners): lock client width, adjust height.
            bool pureVertical = edge is WMSZ_TOP or WMSZ_BOTTOM;
            if (pureVertical)
            {
                int newClientW = (int)Math.Round(clientH * _aspectRatio);
                rect.Right = rect.Left + newClientW + _chromeWidth;
            }
            else
            {
                int newClientH = (int)Math.Round(clientW / _aspectRatio);
                int newH       = newClientH + _chromeHeight;
                bool topDriven = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT;
                if (topDriven) rect.Top    = rect.Bottom - newH;
                else           rect.Bottom = rect.Top    + newH;
            }

            Marshal.StructureToPtr(rect, lParam, false);
        }
        return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
    }
}
