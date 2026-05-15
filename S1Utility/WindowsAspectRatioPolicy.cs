using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace S1Utility;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAspectRatioPolicy : IWindowSizePolicy
{
    private const int  GWLP_WNDPROC      = -4;
    private const uint WM_SIZING         = 0x0214;
    private const int  WMSZ_TOP          = 3, WMSZ_TOPLEFT  = 4, WMSZ_TOPRIGHT = 5;
    private const int  WMSZ_BOTTOM       = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly double _aspectRatio;
    private WndProcDelegate? _wndProc;
    private IntPtr _oldWndProc;
    private IntPtr _hWnd;

    public WindowsAspectRatioPolicy(double aspectRatio) => _aspectRatio = aspectRatio;

    public void Attach(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null) return;
        _hWnd = handle.Handle;
        _wndProc = WndProcHook;
        _oldWndProc = SetWindowLongPtrW(_hWnd, GWLP_WNDPROC,
                          Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    public void Detach()
    {
        if (_hWnd == IntPtr.Zero || _oldWndProc == IntPtr.Zero) return;
        SetWindowLongPtrW(_hWnd, GWLP_WNDPROC, _oldWndProc);
        _hWnd = IntPtr.Zero;
        _oldWndProc = IntPtr.Zero;
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

            // Pure top/bottom drag: lock height, adjust width rightward.
            // All other edges (including corners): lock width, adjust height.
            bool pureVertical = edge is WMSZ_TOP or WMSZ_BOTTOM;
            if (pureVertical)
            {
                rect.Right = rect.Left + (int)Math.Round(h * _aspectRatio);
            }
            else
            {
                int newH = (int)Math.Round(w / _aspectRatio);
                bool topDriven = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT;
                if (topDriven) rect.Top    = rect.Bottom - newH;
                else           rect.Bottom = rect.Top    + newH;
            }

            Marshal.StructureToPtr(rect, lParam, false);
        }
        return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
    }
}
