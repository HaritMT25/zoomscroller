using System;
using System.Threading;

namespace ZoomScroller
{
    /// <summary>Test probe: scripted touch gestures at a screen point, only into a window whose title has a marker.
    ///   probe pinch MARKER cx cy span0 span1 ms
    ///   probe pan   MARKER cx cy span dx dy ms
    ///   probe where MARKER cx cy</summary>
    static class Probe
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        static int CursorShown() { var ci = new CURSORINFO(); ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(ci); GetCursorInfo(ref ci); return ci.flags; }

        /// <summary>"P x0 y0 x1 y1|M x0 y0 x1 y1|H n|U|S ms|Q label" separated by ';', 8 ms per frame.
        /// Q prints the label (a test reads the page then) and holds the fingers still for 500 ms.</summary>
        static int Frames(string script)
        {
            string fs = Environment.GetEnvironmentVariable("FRAME_SLEEP");  // ms after each command (default 8)
            int frameSleep = fs == null ? 8 : int.Parse(fs);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double next = 0;
            Native.timeBeginPeriod(1);
            var f = new Fingers();
            bool ok = true, alternate = false;
            double fx0 = 0, fy0 = 0, fx1 = 0, fy1 = 0, oneX = 0, oneY = 0;
            foreach (var raw in script.Split(';'))
            {
                var w = raw.Trim().Split(' ');
                if (w[0].Length == 0) continue;
                Func<int, double> n = i => double.Parse(w[i], System.Globalization.CultureInfo.InvariantCulture);
                switch (w[0])
                {
                    case "P": ok &= f.Press(n(1), n(2), n(3), n(4)); fx0 = n(1); fy0 = n(2); fx1 = n(3); fy1 = n(4); break;
                    case "M": ok &= f.Move(n(1), n(2), n(3), n(4)); fx0 = n(1); fy0 = n(2); fx1 = n(3); fy1 = n(4); if (alternate) f.Reversed = !f.Reversed; break;
                    case "N": f.Reversed = true; ok &= f.Move(n(1), n(2), n(3), n(4)); f.Reversed = false; break;
                    case "A": alternate = true; break;  // from here every move flips the order fingers are sent in
                    case "O":  // O x y: one finger down (a plain one-finger touch); o x y: move it; u: up
                    case "o":
                    case "u":
                        {
                            var t = new POINTER_TOUCH_INFO[1];
                            t[0].pointerInfo.pointerType = Native.PT_TOUCH;
                            t[0].pointerInfo.pointerId = 0;
                            uint flags = w[0] == "O" ? Native.POINTER_FLAG_DOWN | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT
                                       : w[0] == "o" ? Native.POINTER_FLAG_UPDATE | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT
                                       : Native.POINTER_FLAG_UP;
                            if (w[0] != "u") { oneX = n(1); oneY = n(2); }
                            t[0].pointerInfo.pointerFlags = flags;
                            t[0].pointerInfo.ptPixelLocation = new POINT(Native.Px(oneX), Native.Px(oneY));
                            t[0].touchMask = Native.TOUCH_MASK_CONTACTAREA | Native.TOUCH_MASK_ORIENTATION | Native.TOUCH_MASK_PRESSURE;
                            t[0].rcContact.Left = Native.Px(oneX) - 2; t[0].rcContact.Right = Native.Px(oneX) + 2;
                            t[0].rcContact.Top = Native.Px(oneY) - 2; t[0].rcContact.Bottom = Native.Px(oneY) + 2;
                            t[0].orientation = 90; t[0].pressure = 32000;
                            Fingers.Init();
                            ok &= Native.InjectTouchInput(1, t);
                        }
                        break;
                    case "Z":  // Z dx dy: finger 1 moves by (dx,dy), then finger 0 (two injections, back to back)
                    case "Y":  // Y dx dy: finger 0 first, then finger 1
                        if (w[0] == "Z") { fx1 += n(1); fy1 += n(2); ok &= f.Move(fx0, fy0, fx1, fy1); fx0 += n(1); fy0 += n(2); }
                        else { fx0 += n(1); fy0 += n(2); ok &= f.Move(fx0, fy0, fx1, fy1); fx1 += n(1); fy1 += n(2); }
                        ok &= f.Move(fx0, fy0, fx1, fy1);
                        break;
                    case "F":  // F n dx dy: n moves of both fingers by (dx,dy) back to back, then the frame pause
                        for (int i = 0; i < (int)n(1) && ok; i++)
                        {
                            fx0 += n(2); fy0 += n(3); fx1 += n(2); fy1 += n(3);
                            ok &= f.Move(fx0, fy0, fx1, fy1);
                        }
                        break;
                    case "H": for (int i = 0; i < (int)n(1); i++) { ok &= f.Hold(); Thread.Sleep(8); } next = clock.Elapsed.TotalMilliseconds; break;
                    case "U": ok &= f.Release(); break;
                    case "S": Thread.Sleep((int)n(1)); next += n(1); break;
                    case "Q":
                        Console.WriteLine("Q " + w[1]);
                        Console.Out.Flush();
                        for (int i = 0; i < 31; i++) { if (f.IsDown) ok &= f.Hold(); Thread.Sleep(16); }
                        next = clock.Elapsed.TotalMilliseconds;
                        break;
                }
                // fixed pacing: each command starts frameSleep ms after the previous one (spin the last ms)
                next += frameSleep;
                while (clock.Elapsed.TotalMilliseconds < next - 1.5) Thread.Sleep(1);
                while (clock.Elapsed.TotalMilliseconds < next) { }
            }
            Native.timeEndPeriod(1);
            Console.WriteLine("inject ok={0} err={1}", ok, f.LastError);
            return ok ? 0 : 1;
        }

        /// <summary>Real fingers, printing every touch frame when PROBE_LOG is set.</summary>
        class LoggedFingers : IFingers
        {
            readonly Fingers f = new Fingers();
            readonly System.Diagnostics.Stopwatch clock;
            readonly bool log = Environment.GetEnvironmentVariable("PROBE_LOG") != null;
            public LoggedFingers(System.Diagnostics.Stopwatch clock) { this.clock = clock; }
            public bool IsDown { get { return f.IsDown; } }
            void Log(string k, double x0, double y0, double x1, double y1)
            {
                if (log) Console.WriteLine("  {0,7:0.0} {1} {2},{3} {4},{5}", clock.Elapsed.TotalMilliseconds, k, Native.Px(x0), Native.Px(y0), Native.Px(x1), Native.Px(y1));
            }
            public bool Press(double x0, double y0, double x1, double y1) { Log("down", x0, y0, x1, y1); return f.Press(x0, y0, x1, y1); }
            public bool Move(double x0, double y0, double x1, double y1) { Log("move", x0, y0, x1, y1); return f.Move(x0, y0, x1, y1); }
            public bool Hold() { return f.Hold(); }
            public bool Release() { if (log) Console.WriteLine("  {0,7:0.0} up", clock.Elapsed.TotalMilliseconds); return f.Release(); }
        }

        /// <summary>The real engine with scripted input, ';'-separated:
        ///   E delta (engage at the point with a wheel notch) | Z delta | D dx dy | W ms (run frames) |
        ///   N n dx dy ms (n drags of dx,dy, ms of frames after each) | Q label (print it, run frames 500 ms) | R (let go)</summary>
        static int RunEngine(string script, IntPtr widget, int cx, int cy)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var e = new Engine(new LoggedFingers(clock));
            string step = Environment.GetEnvironmentVariable("MAX_STEP");
            if (step != null) e.MaxStepDip = double.Parse(step, System.Globalization.CultureInfo.InvariantCulture);
            string hold = Environment.GetEnvironmentVariable("HOLD_MS");
            if (hold != null) e.HoldMs = double.Parse(hold, System.Globalization.CultureInfo.InvariantCulture);
            string gap = Environment.GetEnvironmentVariable("START_GAP");
            if (gap != null) e.StartGapMs = double.Parse(gap, System.Globalization.CultureInfo.InvariantCulture);
            RECT page; Native.GetWindowRect(widget, out page);
            RECT mon = Native.MonitorRect(new POINT(cx, cy)), area;
            area.Left = Math.Max(page.Left + 6, mon.Left + 32); area.Top = Math.Max(page.Top + 6, mon.Top + 32);
            area.Right = Math.Min(page.Right - 6, mon.Right - 32); area.Bottom = Math.Min(page.Bottom - 6, mon.Bottom - 32);
            double dpi = Native.GetDpiForWindow(widget) / 96.0;
            e.HitOk = (x, y) => Native.WindowFromPoint(new POINT(Native.Px(x), Native.Px(y))) == widget;
            Native.timeBeginPeriod(1);
            Func<double> now = () => clock.Elapsed.TotalMilliseconds;
            string fms = Environment.GetEnvironmentVariable("FRAME_MS");
            int frameMs = fms == null ? 4 : int.Parse(fms);
            Action<double> frames = ms => { double end = now() + ms; while (now() < end) { e.Step(now()); Thread.Sleep(frameMs); } };
            bool ok = true;
            foreach (var raw in script.Split(';'))
            {
                var w = raw.Trim().Split(' ');
                if (w[0].Length == 0) continue;
                Func<int, double> n = i => double.Parse(w[i], System.Globalization.CultureInfo.InvariantCulture);
                switch (w[0])
                {
                    case "E": ok &= e.Engage(cx, cy, area, dpi, (int)n(1), now()); break;
                    case "Z": e.AddZoom((int)n(1)); break;
                    case "D": e.AddPan(n(1), n(2)); break;
                    case "W": frames(n(1)); break;
                    case "N": for (int i = 0; i < (int)n(1); i++) { e.AddPan(n(2), n(3)); frames(n(4)); } break;
                    case "Q": Console.WriteLine("Q " + w[1]); Console.Out.Flush(); frames(500); break;
                    case "R": e.RequestRelease(); for (int i = 0; i < 500 && e.Step(now()); i++) Thread.Sleep(4); break;
                }
            }
            e.ReleaseNow();
            Native.timeEndPeriod(1);
            Console.WriteLine("inject ok={0} regrips={1} failures={2}", ok, e.Regrips, e.Failures);
            return ok ? 0 : 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        /// <summary>find pid:N - bring that browser's main window to the front and print its page area.</summary>
        static int Find(uint pid)
        {
            IntPtr best = IntPtr.Zero, bestPage = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, l) =>
            {
                uint p; Native.GetWindowThreadProcessId(h, out p);
                if (p != pid || !Native.IsWindowVisible(h)) return true;
                IntPtr page = IntPtr.Zero;
                Native.EnumChildWindows(h, (c, l2) =>
                {
                    if (Native.ClassOf(c) == "Chrome_RenderWidgetHostHWND" && Native.IsWindowVisible(c)) { page = c; return false; }
                    return true;
                }, IntPtr.Zero);
                RECT r; Native.GetWindowRect(h, out r);
                long area = (long)r.Width * r.Height;
                if (page != IntPtr.Zero && area > bestArea) { bestArea = area; best = h; bestPage = page; }
                return true;
            }, IntPtr.Zero);
            if (best == IntPtr.Zero) { Console.WriteLine("no page window"); return 1; }
            // topmost and back: in front of everything without taking the keyboard focus
            Native.SetWindowPos(best, new IntPtr(-1), 0, 0, 0, 0, 0x3 | 0x10);
            Native.SetWindowPos(best, new IntPtr(-2), 0, 0, 0, 0, 0x3 | 0x10);
            RECT pr; Native.GetWindowRect(bestPage, out pr);
            Console.WriteLine("page {0} {1} {2} {3}", pr.Left, pr.Top, pr.Right, pr.Bottom);
            return 0;
        }

        static int Main(string[] a)
        {
            Native.MakeDpiAware();
            if (a[0] == "find") return Find(uint.Parse(a[1].Substring(4)));
            string marker = a[1];
            int cx = int.Parse(a[2]), cy = int.Parse(a[3]);
            IntPtr hit = Native.WindowFromPoint(new POINT(cx, cy));
            IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
            string title = Native.TitleOf(root);
            RECT wr; Native.GetWindowRect(hit, out wr);
            Console.WriteLine("hit {0} root '{1}' proc {2} rect {3} {4} {5} {6}", Native.ClassOf(hit), title, Native.ProcessNameOf(root),
                wr.Left, wr.Top, wr.Right, wr.Bottom);
            // marker: text in the test window's title, or pid:N for the test browser's process
            uint pid; Native.GetWindowThreadProcessId(root, out pid);
            bool mine = marker.StartsWith("pid:") ? pid.ToString() == marker.Substring(4) : title.IndexOf(marker, StringComparison.Ordinal) >= 0;
            if (!mine) { Console.WriteLine("REFUSED: not the test window"); return 2; }
            if (a[0] == "where") return 0;
            if (a[0] == "frames") return Frames(a[4]);
            if (a[0] == "engine") return RunEngine(a[4], hit, cx, cy);
            POINT c0; Native.GetCursorPos(out c0);
            Console.WriteLine("cursor before {0},{1} shown={2}", c0.X, c0.Y, CursorShown());
            Native.timeBeginPeriod(1);
            var f = new Fingers();
            bool ok;
            if (a[0] == "pinch")
            {
                double s0 = double.Parse(a[4]), s1 = double.Parse(a[5]);
                int ms = int.Parse(a[6]);
                ok = f.Press(cx - s0 / 2, cy, cx + s0 / 2, cy);
                int steps = Math.Max(1, ms / 8);
                for (int i = 1; i <= steps && ok; i++)
                {
                    double s = s0 + (s1 - s0) * i / steps;
                    ok = f.Move(cx - s / 2, cy, cx + s / 2, cy);
                    Thread.Sleep(8);
                }
            }
            else
            {
                double s = double.Parse(a[4]), dx = double.Parse(a[5]), dy = double.Parse(a[6]);
                int ms = int.Parse(a[7]);
                ok = f.Press(cx - s / 2, cy, cx + s / 2, cy);
                int steps = Math.Max(1, ms / 8);
                for (int i = 1; i <= steps && ok; i++)
                {
                    double x = cx + dx * i / steps, y = cy + dy * i / steps;
                    ok = f.Move(x - s / 2, y, x + s / 2, y);
                    Thread.Sleep(8);
                }
            }
            for (int i = 0; i < 10 && ok; i++) { ok = f.Hold(); Thread.Sleep(8); }  // stand still: no fling
            bool up = f.Release();
            Native.timeEndPeriod(1);
            Console.WriteLine("inject ok={0} up={1} err={2}", ok, up, f.LastError);
            Thread.Sleep(100);
            POINT c1; Native.GetCursorPos(out c1);
            Console.WriteLine("cursor after {0},{1} shown={2}", c1.X, c1.Y, CursorShown());
            return ok ? 0 : 1;
        }
    }
}
