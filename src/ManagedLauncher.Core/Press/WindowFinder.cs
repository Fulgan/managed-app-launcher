using System.Runtime.InteropServices;

namespace ManagedLauncher.Core.Press;

/// <summary>Finds and activates application windows.</summary>
public static class WindowFinder
{
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int SW_RESTORE = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int DWM_CLOAKED_APP = 1;
    private const uint WM_CLOSE = 0x0010;
    private const ushort VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;

    /// <summary>
    /// First top-level window in Z-order owned by one of the pids that the user could see: visible or minimized,
    /// unowned, not a tool window, titled. Windows an app hid itself (tray apps, app-cloaked windows) are skipped;
    /// windows cloaked by the shell because they sit on another virtual desktop are kept.
    /// </summary>
    public static IntPtr FindMainWindow(IReadOnlySet<int> pids)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd, pids))
                return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Owner pid and title of every window <see cref="FindMainWindow"/> would consider, in Z-order.</summary>
    public static IReadOnlyList<(int Pid, string Title)> VisibleWindows()
    {
        var found = new List<(int, string)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (IsCandidate(hwnd, new HashSet<int> { (int)pid }))
            {
                var title = new System.Text.StringBuilder(GetWindowTextLength(hwnd) + 1);
                GetWindowText(hwnd, title, title.Capacity);
                found.Add(((int)pid, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void PostClose(int pid)
    {
        var set = new HashSet<int> { pid };
        EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd, set))
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Windows only lets the foreground process hand focus away. Attaching to the foreground thread's input
    /// queue, plus a synthetic Alt tap when that is not enough, lifts the foreground lock for this call.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        var foreground = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != ourThread && AttachThreadInput(ourThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(ourThread, fgThread, false);
        }

        if (GetForegroundWindow() != hwnd)
        {
            keybd_event((byte)VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
        }
    }

    private static bool IsCandidate(IntPtr hwnd, IReadOnlySet<int> pids)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (!pids.Contains((int)pid))
            return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            return false;
        if (!IsWindowVisible(hwnd) && !IsIconic(hwnd))
            return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
            return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && (cloaked & DWM_CLOAKED_APP) != 0)
            return false;
        return GetWindowTextLength(hwnd) > 0;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
