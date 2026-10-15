using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZoomScroller
{
    static class Program
    {
        /// <summary>dwExtraInfo the tests put on SendInput events; only a --test instance acts on injected input.</summary>
        public const long TestTag = 0x50494E43;  // "PINC"

        [STAThread]
        static int Main(string[] args)
        {
            bool test = Array.IndexOf(args, "--test") >= 0;
            Native.MakeDpiAware();
            Log.ToConsole = test;
            bool created;
            using (new Mutex(true, test ? "Local\\ZoomScroller.test" : "Local\\ZoomScroller", out created))
            {
                if (!created) { if (test) Console.WriteLine("already running"); return 1; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PinchApp(test, args));
            }
            return 0;
        }
    }

    class PinchApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "ZoomScroller";
        const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        readonly bool test;
        readonly Settings settings;
        readonly Engine engine;
        readonly HookProc mouseProc, keyProc;  // kept as fields: the hooks call them after GC would collect locals
        IntPtr mouseHook, keyHook;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Stopwatch clock = Stopwatch.StartNew();
        NotifyIcon tray;
        IntPtr lastRoot;
        bool lastRootIsTarget;

        double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        public PinchApp(bool test, string[] args)
        {
            this.test = test;
            bool firstRun = !System.IO.File.Exists(Settings.FilePath);
            settings = test ? new Settings() : Settings.Load();
            engine = new Engine(new Fingers());
            ApplySettings();
            if (!Fingers.Init()) Log.Write("touch injection unavailable: error " + Marshal.GetLastWin32Error());

            mouseProc = OnMouse;
            keyProc = OnKey;
            InstallHooks();
            var frames = new Thread(FrameLoop) { IsBackground = true, Name = "frames", Priority = ThreadPriority.AboveNormal };
            frames.Start();

            SystemEvents.SessionSwitch += (s, e) => { if (e.Reason == SessionSwitchReason.SessionUnlock) InstallHooks(); };
            SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) InstallHooks(); };

            if (test)
            {
                int seconds = 60;
                int i = Array.IndexOf(args, "--seconds");
                if (i >= 0 && i + 1 < args.Length) int.TryParse(args[i + 1], out seconds);
                var t = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
                t.Tick += (s, e) => ExitThread();
                t.Start();
                Log.Write("test instance ready");
                return;
            }
            if (firstRun) { SetAutostart(true); settings.Save(); }
            BuildTray();
            Log.Write("started");
        }

        void ApplySettings()
        {
            engine.ZoomPerNotch = settings.ZoomPerNotch;
            engine.InvertPan = settings.InvertPan;
            engine.Momentum = settings.Momentum;
        }

        // ---------------------------------------------------------------- input

        void InstallHooks()
        {
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            IntPtr mod = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero) Log.Write("hook install failed: " + Marshal.GetLastWin32Error());
        }

        static bool Down(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        IntPtr OnMouse(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                long extra = m.dwExtraInfo.ToInt64();
                bool injected = (m.flags & Native.LLMHF_INJECTED) != 0;
                bool touchMade = (extra & 0xFFFFFF00L) == 0xFF515700L;  // mouse input Windows made from touch/pen
                // the real app acts on the real mouse only; a test instance only on the tests' tagged input
                bool mine = test ? injected && extra == Program.TestTag : !injected;
                if (mine && !touchMade)
                {
                    try { if (Handle((int)w, m)) return (IntPtr)1; }
                    catch (Exception e) { Log.Write("mouse hook: " + e); }
                }
            }
            return Native.CallNextHookEx(mouseHook, n, w, l);
        }

        bool Handle(int msg, MSLLHOOKSTRUCT m)
        {
            switch (msg)
            {
                case Native.WM_MOUSEWHEEL:
                    if (!settings.Enabled || !Down(Native.VK_CONTROL) || Down(Native.VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN))
                        return false;
                    int delta = (short)((m.mouseData >> 16) & 0xFFFF);
                    if (delta == 0) return false;
                    if (engine.Engaged) { engine.AddZoom(delta); wake.Set(); return true; }
                    return TryEngage(m.pt, delta);

                case Native.WM_MOUSEMOVE:
                    if (!engine.CapturesMouse) return false;
                    // the cursor stays where the touch put it; the would-be motion pans instead
                    POINT cur;
                    Native.GetCursorPos(out cur);
                    engine.AddPan(m.pt.X - cur.X, m.pt.Y - cur.Y);
                    wake.Set();
                    return true;

                case Native.WM_LBUTTONDOWN:
                case Native.WM_RBUTTONDOWN:
                case Native.WM_MBUTTONDOWN:
                case Native.WM_XBUTTONDOWN:
                    if (engine.Engaged) { engine.ReleaseNow(); Log.Write("released for a click"); }
                    return false;
            }
            return false;
        }

        bool TryEngage(POINT pt, int delta)
        {
            IntPtr hit = Native.WindowFromPoint(pt);
            if (hit == IntPtr.Zero || Native.ClassOf(hit) != "Chrome_RenderWidgetHostHWND") return false;
            IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
            if (root != lastRoot)
            {
                lastRoot = root;
                lastRootIsTarget = Array.IndexOf(settings.Apps, Native.ProcessNameOf(root)) >= 0;
            }
            if (!lastRootIsTarget) return false;
            if (!Fingers.Init()) return false;  // no touch injection: leave Ctrl + wheel alone

            RECT page, area;
            Native.GetWindowRect(hit, out page);
            RECT mon = Native.MonitorRect(pt);
            // stay off the page border, and off the screen edges where Windows has its own touch swipes
            area.Left = Math.Max(page.Left + 6, mon.Left + 32);
            area.Top = Math.Max(page.Top + 6, mon.Top + 32);
            area.Right = Math.Min(page.Right - 6, mon.Right - 32);
            area.Bottom = Math.Min(page.Bottom - 6, mon.Bottom - 32);
            double dpi = Native.GetDpiForWindow(hit) / 96.0;
            if (dpi <= 0) dpi = 1;
            IntPtr widget = hit;
            engine.HitOk = (x, y) => Native.WindowFromPoint(new POINT((int)Math.Round(x), (int)Math.Round(y))) == widget;
            if (engine.Engage(pt.X, pt.Y, area, dpi, delta, Now))
            {
                wake.Set();
                if (test) Log.Write(string.Format("engaged at {0},{1} area {2} dpi {3}", pt.X, pt.Y, area, dpi));
            }
            else Log.Write(string.Format("no room to pinch at {0},{1} in {2}", pt.X, pt.Y, area));
            return true;  // over a page Ctrl + wheel never falls back to the browser zoom
        }

        IntPtr OnKey(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0 && engine.Engaged)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
                int msg = (int)w, vk = (int)k.vkCode;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                if (vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL || vk == Native.VK_CONTROL)
                {
                    int other = vk == Native.VK_RCONTROL ? Native.VK_LCONTROL : Native.VK_RCONTROL;
                    if (up && !Down(other)) { engine.RequestRelease(); wake.Set(); }
                }
                else if (!up && vk != VK_LSHIFT && vk != VK_RSHIFT && vk != Native.VK_SHIFT)
                {
                    engine.RequestRelease();  // e.g. Ctrl+C in the middle: let go first
                    wake.Set();
                }
            }
            return Native.CallNextHookEx(keyHook, n, w, l);
        }

        void FrameLoop()
        {
            while (true)
            {
                wake.WaitOne();
                if (!engine.Engaged) continue;
                Native.timeBeginPeriod(1);  // 4 ms frames only while a gesture runs
                try
                {
                    double ctrlUpSince = -1;
                    while (engine.Step(Now))
                    {
                        // safety net: a Ctrl key-up the hook never saw (lock screen, UAC) still ends the gesture
                        if (engine.CapturesMouse && !Down(Native.VK_CONTROL))
                        {
                            if (ctrlUpSince < 0) ctrlUpSince = Now;
                            else if (Now - ctrlUpSince > 150) engine.RequestRelease();
                        }
                        else ctrlUpSince = -1;
                        Thread.Sleep(4);
                    }
                    if (test)
                    {
                        var s = engine.Snapshot();
                        Log.Write(string.Format("released, cursor {0:0},{1:0} pan {4:0},{5:0} regrips {2} failures {3}",
                            s[0], s[1], engine.Regrips, engine.Failures, engine.PanTotalX, engine.PanTotalY));
                    }
                }
                catch (Exception e) { Log.Write("frames: " + e); engine.ReleaseNow(); }
                finally { Native.timeEndPeriod(1); }
            }
        }

        // ---------------------------------------------------------------- tray

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            var enabled = new ToolStripMenuItem("Pinch zoom with Ctrl + wheel") { Checked = settings.Enabled, CheckOnClick = true };
            enabled.CheckedChanged += (s, e) => { settings.Enabled = enabled.Checked; settings.Save(); UpdateTray(); };
            menu.Items.Add(enabled);
            menu.Items.Add(new ToolStripSeparator());

            var speed = new ToolStripMenuItem("Zoom speed");
            foreach (var p in new[] { Tuple.Create("Gentle", 1.12), Tuple.Create("Normal", 1.2), Tuple.Create("Fast", 1.3) })
            {
                var item = new ToolStripMenuItem(p.Item1) { Tag = p.Item2, Checked = Math.Abs(settings.ZoomPerNotch - p.Item2) < 0.005 };
                item.Click += (s, e) =>
                {
                    foreach (ToolStripMenuItem other in speed.DropDownItems) other.Checked = other == item;
                    settings.ZoomPerNotch = (double)item.Tag; settings.Save(); ApplySettings();
                };
                speed.DropDownItems.Add(item);
            }
            menu.Items.Add(speed);

            var follow = new ToolStripMenuItem("Page follows the mouse while zoomed") { Checked = !settings.InvertPan, CheckOnClick = true };
            follow.CheckedChanged += (s, e) => { settings.InvertPan = !follow.Checked; settings.Save(); ApplySettings(); };
            menu.Items.Add(follow);
            var glide = new ToolStripMenuItem("Keep gliding after letting go of Ctrl") { Checked = settings.Momentum, CheckOnClick = true };
            glide.CheckedChanged += (s, e) => { settings.Momentum = glide.Checked; settings.Save(); ApplySettings(); };
            menu.Items.Add(glide);
            var autostart = new ToolStripMenuItem("Start with Windows") { Checked = AutostartEnabled(), CheckOnClick = true };
            autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);
            menu.Items.Add(autostart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("How to use", null, (s, e) => ShowHelp());
            menu.Items.Add("Quit", null, (s, e) => ExitThread());

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => enabled.Checked = !enabled.Checked;
            UpdateTray();
            new Thread(PromoteTrayIcon) { IsBackground = true }.Start();
        }

        void UpdateTray()
        {
            var old = tray.Icon;
            tray.Icon = DrawIcon(settings.Enabled);
            tray.Text = settings.Enabled ? "ZoomScroller: Ctrl + wheel zooms the page" : "ZoomScroller: off";
            if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
        }

        static Icon DrawIcon(bool on)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color c = on ? Color.FromArgb(0, 150, 255) : Color.FromArgb(140, 140, 140);
                float u = size / 16f;
                using (var pen = new Pen(c, 1.8f * u))
                using (var handle = new Pen(c, 2.6f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawEllipse(pen, 1.5f * u, 1.5f * u, 9f * u, 9f * u);
                    g.DrawLine(handle, 9.6f * u, 9.6f * u, 14.2f * u, 14.2f * u);
                    g.DrawLine(pen, 3.8f * u, 6f * u, 8.2f * u, 6f * u);
                    g.DrawLine(pen, 6f * u, 3.8f * u, 6f * u, 8.2f * u);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void ShowHelp()
        {
            MessageBox.Show(
                "Works in Chrome and Edge, like a touchpad pinch:\n\n" +
                "  Hold Ctrl and turn the wheel up    zoom in where the cursor is\n" +
                "  Keep Ctrl held and move the mouse   move around the zoomed page\n" +
                "  Ctrl + wheel down                   zoom back out (stops at 100%)\n" +
                "  Let go of Ctrl                      the page stays zoomed\n\n" +
                "While zoomed, the plain wheel scrolls inside the zoomed view.\n" +
                "Ctrl + plus / minus still change the browser's own zoom.\n\n" +
                "Some sites (YouTube) block touch zoom. The helper extension in the\n" +
                "'extension' folder lifts that: chrome://extensions > Developer mode >\n" +
                "Load unpacked.\n\n" +
                "Settings: " + Settings.FilePath,
                "ZoomScroller", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Windows 11 hides new tray icons under the ^ overflow: show ours once, unless the user chose.</summary>
        static void PromoteTrayIcon()
        {
            string exe = Application.ExecutablePath;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(1000);
                try
                {
                    using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                    {
                        if (root == null) continue;
                        foreach (var name in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(name, true))
                            {
                                var path = k == null ? null : k.GetValue("ExecutablePath") as string;
                                if (path == null || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                                if (k.GetValue("IsPromoted") == null) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                return;
                            }
                    }
                }
                catch (Exception e) { Log.Write("tray promote: " + e.Message); return; }
            }
        }

        static bool AutostartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
            }
            catch (Exception e) { Log.Write("autostart: " + e.Message); }
        }

        protected override void ExitThreadCore()
        {
            engine.ReleaseNow();
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.ExitThreadCore();
        }
    }
}
