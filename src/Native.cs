using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MousePinch
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public bool Contains(int x, int y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public override string ToString() { return string.Format("[{0},{1} {2}x{3}]", Left, Top, Width, Height); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public int pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static class Native
    {
        public const int PT_TOUCH = 2;
        public const uint POINTER_FLAG_INRANGE = 0x2, POINTER_FLAG_INCONTACT = 0x4,
            POINTER_FLAG_DOWN = 0x10000, POINTER_FLAG_UPDATE = 0x20000, POINTER_FLAG_UP = 0x40000;
        public const uint TOUCH_MASK_CONTACTAREA = 0x1, TOUCH_MASK_ORIENTATION = 0x2, TOUCH_MASK_PRESSURE = 0x4;
        public const uint TOUCH_FEEDBACK_NONE = 0x3;

        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207,
            WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_MOUSEHWHEEL = 0x20E;
        public const uint LLMHF_INJECTED = 0x1;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
        public const uint GA_ROOT = 2;

        [DllImport("user32.dll", SetLastError = true)] public static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref int size);

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        /// <summary>Touch injection takes whole pixels; everything that predicts what Chrome sees rounds this way.</summary>
        public static int Px(double v) { return (int)Math.Floor(v + 0.5); }  // not banker's: equal fractions round alike

        public static void MakeDpiAware()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
        }

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TitleOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ProcessNameOf(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                return System.IO.Path.GetFileName(sb.ToString()).ToLowerInvariant();
            }
            finally { CloseHandle(h); }
        }

        public static RECT MonitorRect(POINT p)
        {
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(MonitorFromPoint(p, 2 /* NEAREST */), ref mi);
            return mi.rcMonitor;
        }
    }
}
