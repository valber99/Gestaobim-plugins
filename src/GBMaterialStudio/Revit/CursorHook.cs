using System.Runtime.InteropServices;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>
/// Keeps the eyedropper cursor while a pick runs. A WM_SETCURSOR hook reapplies it right after Revit sets
/// its own, so it never flickers. Only the UI thread's client area is affected.
/// </summary>
public static class CursorHook
{
    private const int WH_CALLWNDPROCRET = 12;
    private const uint WM_SETCURSOR = 0x0020;
    private const int HTCLIENT = 1;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CWPRETSTRUCT
    {
        public IntPtr lResult;
        public IntPtr lParam;
        public IntPtr wParam;
        public uint message;
        public IntPtr hwnd;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorFromFile(string path);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCursor();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Held in a static so the garbage collector never frees the delegate while the hook is installed.
    private static readonly HookProc Proc = Hook;
    private static IntPtr _hook = IntPtr.Zero;
    private static IntPtr _cursor = IntPtr.Zero;

    public static bool Active => _hook != IntPtr.Zero;

    public static bool Install(string cursorFile)
    {
        Uninstall();
        _cursor = LoadCursorFromFile(cursorFile);
        if (_cursor == IntPtr.Zero) return false;
        _hook = SetWindowsHookEx(WH_CALLWNDPROCRET, Proc, IntPtr.Zero, GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
        {
            DestroyCursor(_cursor);
            _cursor = IntPtr.Zero;
            return false;
        }
        SetCursor(_cursor);
        return true;
    }

    /// <summary>Covers cursor changes made outside WM_SETCURSOR.</summary>
    public static void Enforce()
    {
        if (_cursor != IntPtr.Zero && GetCursor() != _cursor)
            SetCursor(_cursor);
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (_cursor != IntPtr.Zero)
        {
            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32512)); // IDC_ARROW
            DestroyCursor(_cursor);
            _cursor = IntPtr.Zero;
        }
    }

    private static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _cursor != IntPtr.Zero)
        {
            var data = Marshal.PtrToStructure<CWPRETSTRUCT>(lParam);
            if (data.message == WM_SETCURSOR && ((long)data.lParam & 0xFFFF) == HTCLIENT)
                SetCursor(_cursor);
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
