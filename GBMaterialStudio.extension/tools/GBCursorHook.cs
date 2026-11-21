using System;
using System.Runtime.InteropServices;

namespace GestaoBim.Native
{
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
    public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")]
    public static extern IntPtr GetCursor();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly HookProc Proc = Hook;
    private static IntPtr hook = IntPtr.Zero;
    private static IntPtr cursor = IntPtr.Zero;

    public static bool Active { get { return hook != IntPtr.Zero; } }
    public static IntPtr Cursor { get { return cursor; } }

    public static bool Install(string path)
    {
        Uninstall();
        cursor = LoadCursorFromFile(path);
        if (cursor == IntPtr.Zero) return false;
        hook = SetWindowsHookEx(WH_CALLWNDPROCRET, Proc, IntPtr.Zero, GetCurrentThreadId());
        if (hook == IntPtr.Zero)
        {
            DestroyCursor(cursor);
            cursor = IntPtr.Zero;
            return false;
        }
        SetCursor(cursor);
        return true;
    }

    public static void Enforce()
    {
        if (cursor != IntPtr.Zero && GetCursor() != cursor) SetCursor(cursor);
    }

    public static void Uninstall()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        if (cursor != IntPtr.Zero)
        {
            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32512));
            DestroyCursor(cursor);
            cursor = IntPtr.Zero;
        }
    }

    private static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && cursor != IntPtr.Zero)
        {
            CWPRETSTRUCT data = (CWPRETSTRUCT)Marshal.PtrToStructure(lParam, typeof(CWPRETSTRUCT));
            if (data.message == WM_SETCURSOR && ((long)data.lParam & 0xFFFF) == HTCLIENT)
                SetCursor(cursor);
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }
}
}
