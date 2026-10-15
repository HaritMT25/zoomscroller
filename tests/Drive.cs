using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MousePinch
{
    /// <summary>Test driver: tagged SendInput (only a --test instance of Mouse Pinch acts on it).
    ///   drive cursor                     print the cursor position
    ///   drive setcursor X Y
    ///   drive "at X Y; ctrl down; wheel 120; move 30 -20; sleep 50; ctrl up; key 43; click"
    /// "ctrl down" is let go when the run ends; "ctrl hold" stays down until a later "ctrl up".</summary>
    static class Drive
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public int type; public UNION u; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        static readonly IntPtr Tag = new IntPtr(0x50494E43);

        static void Send(INPUT i)
        {
            if (SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) != 1)
                throw new Exception("SendInput failed: " + Marshal.GetLastWin32Error());
        }

        static void Mouse(uint flags, int dx, int dy, int data)
        {
            var i = new INPUT { type = 0 };
            i.u.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Tag };
            Send(i);
        }

        static void Key(int vk, bool up)
        {
            var i = new INPUT { type = 1 };
            i.u.ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? 2u : 0u, dwExtraInfo = Tag };
            Send(i);
        }

        static int Main(string[] a)
        {
            Native.MakeDpiAware();
            POINT p;
            if (a[0] == "cursor") { Native.GetCursorPos(out p); Console.WriteLine("{0} {1}", p.X, p.Y); return 0; }
            if (a[0] == "setcursor") { Native.SetCursorPos(int.Parse(a[1]), int.Parse(a[2])); return 0; }
            bool ctrl = false;
            try
            {
                foreach (var raw in a[0].Split(';'))
                {
                    var w = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length == 0) continue;
                    switch (w[0])
                    {
                        case "at": Native.SetCursorPos(int.Parse(w[1]), int.Parse(w[2])); break;
                        case "ctrl":
                            // "hold" stays down after this run (a later run sends "ctrl up"); "down" is released at the end
                            Key(0xA2, w[1] == "up");
                            ctrl = w[1] == "down";
                            break;
                        case "wheel": Mouse(0x800, 0, 0, int.Parse(w[1])); break;
                        case "move": Mouse(0x1, int.Parse(w[1]), int.Parse(w[2]), 0); break;
                        case "click": Mouse(0x2, 0, 0, 0); Mouse(0x4, 0, 0, 0); break;
                        case "key": int vk = Convert.ToInt32(w[1], 16); Key(vk, false); Key(vk, true); break;
                        case "sleep": Thread.Sleep(int.Parse(w[1])); break;
                        case "pos": Native.GetCursorPos(out p); Console.WriteLine("pos {0} {1}", p.X, p.Y); break;
                        default: throw new Exception("unknown step " + raw);
                    }
                }
            }
            finally { if (ctrl) Key(0xA2, true); }  // never leave Ctrl stuck down
            return 0;
        }
    }
}
