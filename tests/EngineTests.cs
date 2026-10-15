using System;
using System.Collections.Generic;

namespace ZoomScroller
{
    /// <summary>Engine behaviour against fake fingers feeding a model of Chrome's pinch handling.
    /// Run: bin\engine_tests.exe (exit code = failures).</summary>
    static class EngineTests
    {
        class Ev { public string Kind; public double X0, Y0, X1, Y1, T; }

        /// <summary>Records every touch frame and plays it through Chrome's rules (ui/events/gesture_detection, measured
        /// in tests/phase_test.mjs), on whole pixels like real touch input:
        /// - scroll: starts once the fingers' midpoint is ScrollSlop from where it was at touch-down (that much of the
        ///   first move is swallowed), or when a pinch starts (nothing swallowed); then it follows the midpoint;
        /// - rails: while no pinch is on, the first time finger 0 is SnapSlop from where it went down, a mostly
        ///   horizontal (vertical) move locks the scroll to that axis (more than 2 x SnapSlop on both: never); a lock
        ///   breaks once the locked-out axis gathers Channel px of motion before the other one does;
        /// - pinch: starts once the span changed by the span slop (swallowed; the excess zooms), then scales by the
        ///   span ratio about the midpoint; it ends if the span drops under Chrome's minimum.
        /// The page maps to the screen as screen = K * page + O. Not modelled: what drags along the line between the
        /// fingers cost in real Chrome (the engine turns the fingers across drags; a test checks that instead).</summary>
        class FakeChrome : IFingers
        {
            public readonly List<Ev> Log = new List<Ev>();
            public double Now, SpanSlop, ScrollSlop, SnapSlop, Channel, MinSpan, EdgeMargin, K = 1, Ox, Oy;
            public RECT Page;  // the page's viewport, for Chrome's snapping of pinch anchors to its edges
            double adjX, adjY;
            public int FailMovesAt = -1;
            public bool Railed;  // an axis lock swallowed some motion
            public bool IsDown { get; private set; }
            bool pinching, scrolling;
            int snap;  // 0 undecided, 1 horizontal, 2 vertical, 3 none
            double accX, accY, span0, pmx, pmy, pspan, d0x, d0y, d1x, d1y;
            Ev last;

            void Add(string k, double a, double b, double c, double d)
            {
                last = new Ev { Kind = k, X0 = a, Y0 = b, X1 = c, Y1 = d, T = Now };
                Log.Add(last);
            }
            public bool Press(double a, double b, double c, double d)
            {
                if (IsDown) throw new Exception("press while down");
                Add("press", a, b, c, d);
                a = Native.Px(a); b = Native.Px(b); c = Native.Px(c); d = Native.Px(d);
                IsDown = true; pinching = scrolling = false; snap = 0; accX = accY = 0;
                d0x = a; d0y = b; d1x = c; d1y = d;
                span0 = Dist(a, b, c, d); pmx = (a + c) / 2; pmy = (b + d) / 2;
                return true;
            }
            public bool Move(double a, double b, double c, double d)
            {
                if (!IsDown) throw new Exception("move while up");
                if (FailMovesAt >= 0 && Log.Count >= FailMovesAt) { FailMovesAt = -1; IsDown = false; return false; }
                Add("move", a, b, c, d);
                a = Native.Px(a); b = Native.Px(b); c = Native.Px(c); d = Native.Px(d);
                double mx = (a + c) / 2, my = (b + d) / 2, s = Dist(a, b, c, d);
                // axis lock: decided from finger 0 only, and not while a pinch is on
                if (!pinching && snap == 0)
                {
                    double dx = Math.Abs(a - d0x), dy = Math.Abs(b - d0y);
                    if (dx * dx + dy * dy > SnapSlop * SnapSlop)
                    {
                        if (dy == 0 || (dx / dy > 1.25 && dy < 2 * SnapSlop)) snap = 1;
                        else if (dx == 0 || (dy / dx > 1.25 && dx < 2 * SnapSlop)) snap = 2;
                    }
                    if (snap == 0 && dx > 2 * SnapSlop && dy > 2 * SnapSlop) snap = 3;
                }
                if (pinching && s < MinSpan) { pinching = false; span0 = s; }
                if (!pinching && Math.Abs(s - span0) > SpanSlop && s >= MinSpan)
                {
                    pinching = true; pspan = span0 + Math.Sign(s - span0) * SpanSlop;
                    if (!scrolling) { scrolling = true; pmx = mx; pmy = my; }  // a pinch starts the scroll: from here 1:1
                    // a pinch starting near the page's edge is zoomed about the edge, for the whole pinch
                    adjX = adjY = 0;
                    if (Page.Width > 0)
                    {
                        if (mx - Page.Left < EdgeMargin) adjX = Page.Left - mx; else if (Page.Right - mx < EdgeMargin) adjX = Page.Right - mx;
                        if (my - Page.Top < EdgeMargin) adjY = Page.Top - my; else if (Page.Bottom - my < EdgeMargin) adjY = Page.Bottom - my;
                    }
                }
                double sx = 0, sy = 0;
                if (!scrolling)
                {
                    double ex = mx - (d0x + d1x) / 2, ey = my - (d0y + d1y) / 2, e = Math.Sqrt(ex * ex + ey * ey);
                    if (e > ScrollSlop)
                    {
                        scrolling = true;
                        sx = ex * (e - ScrollSlop) / e;
                        sy = ey * (e - ScrollSlop) / e;
                    }
                }
                else { sx = mx - pmx; sy = my - pmy; }
                if (snap == 1 || snap == 2)
                {
                    accX += Math.Abs(sx); accY += Math.Abs(sy);
                    if (snap == 1) { if (accY > Channel) snap = 3; else if (accX > Channel) accX = accY = 0; }
                    else { if (accX > Channel) snap = 3; else if (accY > Channel) accX = accY = 0; }
                    if (snap == 1 && sy != 0) { sy = 0; Railed = true; }
                    if (snap == 2 && sx != 0) { sx = 0; Railed = true; }
                }
                Ox += sx; Oy += sy;
                if (pinching)
                {
                    double r = s / pspan, zx = mx + adjX, zy = my + adjY;
                    K *= r;
                    Ox = zx + r * (Ox - zx);
                    Oy = zy + r * (Oy - zy);
                    pspan = s;
                }
                if (Environment.GetEnvironmentVariable("ENGINE_DEBUG2") != null)
                    Console.WriteLine("    {0,6:0} move {1},{2} {3},{4}  mid {5:0.0},{6:0.0} span {7:0.0} pinch {8} scroll {9} snap {10}  K {11:0.00000}  under(1000,500) {12:0.00},{13:0.00}",
                        Now, a, b, c, d, mx, my, s, pinching ? 1 : 0, scrolling ? 1 : 0, snap, K, (1000 - Ox) / K, (500 - Oy) / K);
                pmx = mx; pmy = my;
                return true;
            }
            public bool Hold() { if (!IsDown) throw new Exception("hold while up"); Add("hold", last.X0, last.Y0, last.X1, last.Y1); return true; }
            public bool Release() { if (IsDown) Add("release", last.X0, last.Y0, last.X1, last.Y1); IsDown = false; return true; }
            /// <summary>Page point under a screen point.</summary>
            public double[] Under(double x, double y) { return new[] { (x - Ox) / K, (y - Oy) / K }; }
        }

        static int failures, checks;
        static void Check(bool ok, string what)
        {
            checks++;
            if (!ok) { failures++; Console.WriteLine("FAIL " + what); }
        }

        static RECT Area(int l, int t, int r, int b) { var a = new RECT(); a.Left = l; a.Top = t; a.Right = r; a.Bottom = b; return a; }
        static double Dist(double x0, double y0, double x1, double y1) { return Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); }
        static double Span(Ev e) { return Dist(e.X0, e.Y0, e.X1, e.Y1); }
        static bool Near(double[] p, double[] q, double tol) { return Math.Abs(p[0] - q[0]) <= tol && Math.Abs(p[1] - q[1]) <= tol; }
        static string Fmt(double[] p) { return string.Format("{0:0.00},{1:0.00}", p[0], p[1]); }

        static void Run(Engine e, FakeChrome f, ref double now, double ms)
        {
            for (double end = now + ms; now < end; now += 4)
            {
                f.Now = now; e.Step(now);
                if (Environment.GetEnvironmentVariable("ENGINE_DEBUG3") != null && now <= 80)
                {
                    var sn = e.Snapshot();
                    var fi = typeof(Engine).GetField("owedX", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var dbx = typeof(Engine).GetField("downBx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Console.WriteLine("    t {0,3} fingers {1:0.00},{2:0.00} {3:0.00},{4:0.00} pend {5:0.000},{6:0.000} owed {7:0.000} downBx {8} slop {9}", now, sn[2], sn[3], sn[4], sn[5], sn[7], sn[8], fi.GetValue(e), dbx.GetValue(e), e.ScrollSlopPx);
                }
            }
        }

        const double Dpi = 1.25;
        static FakeChrome Chrome()
        {
            return new FakeChrome { SpanSlop = Engine.ChromeSpanSlopDip * Dpi, ScrollSlop = Engine.ChromeScrollSlopDip * Dpi,
                                    SnapSlop = 15 * Dpi, Channel = 67.5 * Dpi, MinSpan = Engine.ChromeMinSpanDip * Dpi,
                                    EdgeMargin = 100 * Dpi };
        }
        static Engine Make(FakeChrome f) { return new Engine(f) { ZoomPerNotch = 1.2 }; }
        // whole-pixel finger positions: a gesture can end up to a pixel of span off (~0.4%; the next one repays it)
        const double ZoomTol = 0.005;
        const double PanTol = 1.5;  // px

        static int Main()
        {
            RECT area = Area(100, 100, 1900, 1000);

            {   // one notch: the page zooms x1.2 around the cursor and the cursor's page point stays put
                var f = Chrome(); var e = Make(f); double now = 0;
                var before = f.Under(1000, 500);
                Check(e.Engage(1000, 500, area, Dpi, 120, now), "engage");
                Run(e, f, ref now, 400);
                Check(Math.Abs(f.K / 1.2 - 1) < ZoomTol, "one notch = x1.2, got " + f.K);
                Check(Near(before, f.Under(1000, 500), 0.5), "point under the cursor stays: " + Fmt(before) + " -> " + Fmt(f.Under(1000, 500)));
                Check(Span(f.Log[0]) >= Engine.ChromeMinSpanDip * Dpi, "touch-down span >= Chrome's minimum");
                var moves = f.Log.FindAll(x => x.Kind == "move");
                Func<Ev, double[]> midOf = m => new[] { (m.X0 + m.X1) / 2, (m.Y0 + m.Y1) / 2 };
                var p0 = f.Log[0];
                double moved0 = Dist(p0.X0, p0.Y0, moves[0].X0, moves[0].Y0), moved1 = Dist(p0.X1, p0.Y1, moves[0].X1, moves[0].Y1);
                double opened = Span(moves[0]) - Span(p0);
                Check(moved0 < 15 * Dpi && opened > e.SlopPx + 0.5 && opened < e.SlopPx + 3.5,
                    "the start widens past the span slop with finger 0 moving less than the axis-lock slop (" + moved0.ToString("0.0") + " px)");
                Check(Dist(midOf(p0)[0], midOf(p0)[1], midOf(moves[0])[0], midOf(moves[0])[1]) < e.ScrollSlopPx - 2 && Near(midOf(moves[0]), new[] { 1000.0, 500 }, 1),
                    "its midpoint moves less than the scroll slop and ends on the cursor");
                Check(Math.Abs(Span(moves[1]) - Span(p0) - e.SlopPx) <= 1 && Near(midOf(moves[1]), midOf(moves[0]), 1),
                    "then it narrows about the midpoint to Chrome's reference span (span +" + (Span(moves[1]) - Span(p0)).ToString("0.00") + ")");
                Check(moves[0].T - p0.T >= e.StartGapMs && moves[1].T - moves[0].T >= e.StartGapMs, "with pauses Chrome can't merge");
                Check(e.Engaged && f.IsDown, "fingers stay down while Ctrl is held");
            }

            {   // the pinch's midpoint lands on the cursor (zoom = a plain pinch about it, held still by Chrome itself),
                // also by the page's edge, where Chrome then pins the edge as in any touch pinch there
                foreach (var c in new[] { new[] { 150.0, 550.0 }, new[] { 1850.0, 300.0 }, new[] { 900.0, 150.0 }, new[] { 400.0, 950.0 }, new[] { 1000.0, 500.0 } })
                {
                    var f = Chrome(); var e = Make(f); double now = 0;
                    Check(e.Engage(c[0], c[1], Area(106, 106, 1894, 994), Dpi, 120, now), "engage at " + c[0] + "," + c[1]);
                    Run(e, f, ref now, 60);
                    var w = f.Log.FindAll(x => x.Kind == "move")[0];
                    double mx = (Native.Px(w.X0) + Native.Px(w.X1)) / 2.0, my = (Native.Px(w.Y0) + Native.Px(w.Y1)) / 2.0;
                    Check(Math.Abs(mx - c[0]) <= 1 && Math.Abs(my - c[1]) <= 1, "pinch starts with its midpoint on the cursor at " + c[0] + "," + c[1] + " (" + mx + "," + my + ")");
                }
            }

            {   // zooming near an edge: fingers on one side, still anchored at the cursor
                var f = Chrome(); var e = Make(f); double now = 0;
                var before = f.Under(110, 550);
                Check(e.Engage(110, 550, area, Dpi, 120, now), "engage at the left edge");
                for (int i = 0; i < 5; i++) { e.AddZoom(120); Run(e, f, ref now, 40); }
                Run(e, f, ref now, 400);
                Check(Math.Abs(f.K / Math.Pow(1.2, 6) - 1) < 2 * ZoomTol, "6 notches at the edge = x2.99, got " + f.K);
                Check(Near(before, f.Under(110, 550), 1.5), "edge zoom anchored: " + Fmt(before) + " -> " + Fmt(f.Under(110, 550)));
            }

            {   // drag: the page slides under the fixed cursor by exactly the mouse motion (grab); inverted: opposite
                foreach (bool invert in new[] { false, true })
                {
                    var f = Chrome(); var e = Make(f); e.InvertPan = invert; double now = 0;
                    e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                    double k = f.K;
                    var u0 = f.Under(1000, 500);
                    for (int i = 0; i < 10; i++) { e.AddPan(3, -2); Run(e, f, ref now, 8); }
                    Run(e, f, ref now, 40);
                    var u1 = f.Under(1000, 500);
                    double sign = invert ? -1 : 1, mx = (u0[0] - u1[0]) * f.K, my = (u0[1] - u1[1]) * f.K;
                    Check(Math.Abs(mx - 30 * sign) < PanTol && Math.Abs(my + 20 * sign) < PanTol && !f.Railed,
                        (invert ? "inverted " : "") + "page moves with the mouse: " + mx.ToString("0.00") + "," + my.ToString("0.00"));
                    // turning the fingers across the drag lands on whole pixels: the span can move a fraction of one
                    Check(Math.Abs(f.K / k - 1) < 0.002, "dragging doesn't zoom: x" + (f.K / k).ToString("0.00000"));
                }
            }

            {   // zoom after a drag is still about the cursor (not about where the fingers went)
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                e.AddPan(-300, 120); Run(e, f, ref now, 200);
                var before = f.Under(1000, 500);
                e.AddZoom(240); Run(e, f, ref now, 400);
                Check(Near(before, f.Under(1000, 500), 1), "zoom after drag anchored at the cursor: " + Fmt(before) + " -> " + Fmt(f.Under(1000, 500)));
            }

            {   // many short pinches add up: whole-pixel leftovers are cancelled, not accumulated
                var f = Chrome(); var e = Make(f); double now = 0;
                for (int g = 0; g < 20; g++)
                {
                    double k0 = f.K;
                    e.Engage(700 + 10 * g, 450 + 7 * g, area, Dpi, 120, now);
                    Run(e, f, ref now, 300);
                    e.RequestRelease();
                    Run(e, f, ref now, 200);
                    if (Environment.GetEnvironmentVariable("ENGINE_DEBUG") != null)
                        Console.WriteLine("gesture {0}: x{1:0.00000} down {2:0.0} log {3}", g, f.K / k0, Span(f.Log[f.Log.FindLastIndex(x => x.Kind == "press")]), f.Log.Count);
                }
                double got = Math.Log(f.K) / Math.Log(1.2);
                Check(Math.Abs(got - 20) < 0.05, "20 one-notch pinches zoom 1.2^20, got 1.2^" + got.ToString("0.000"));
            }

            {   // in and out many times in one grip: leftovers are repaid by the next zoom, so it comes back to x1
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(701, 451, area, Dpi, 120, now); Run(e, f, ref now, 300);
                for (int i = 0; i < 20; i++) { e.AddZoom(i % 2 == 0 ? -120 : 120); Run(e, f, ref now, 250); }
                e.AddZoom(-120); Run(e, f, ref now, 300);
                Check(e.Regrips == 0, "in/out stays in one grip (" + e.Regrips + ")");
                Check(Math.Abs(Math.Log(f.K)) < 0.003, "20 x in/out returns to x1: " + f.K.ToString("0.00000"));
            }

            {   // many small notches in one grip, each settling: leftovers repaid along the way, not summed
                var f = Chrome(); var e = Make(f); e.ZoomPerNotch = 1.03; double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                for (int i = 0; i < 39; i++) { e.AddZoom(120); Run(e, f, ref now, 250); }
                Check(e.Regrips == 0, "40 small notches stay in one grip (" + e.Regrips + ")");
                Check(Math.Abs(Math.Log(f.K) / (40 * Math.Log(1.03)) - 1) < 0.003, "40 notches of x1.03 = x" + Math.Pow(1.03, 40).ToString("0.000") + ", got x" + f.K.ToString("0.000"));
            }

            {   // diagonal fingers (cursor in a corner): their rounded touch-down span is off; corrected each time
                var f = Chrome(); var e = Make(f); double now = 0;
                var sq = Area(100, 100, 1000, 1000);
                for (int g = 0; g < 20; g++)
                {
                    e.Engage(110 + g, 112 + g, sq, Dpi, 120, now);
                    var down = f.Log[f.Log.FindLastIndex(x => x.Kind == "press")];
                    if (g == 0) Check(down.X0 != down.X1 && down.Y0 != down.Y1, "corner grip is diagonal");
                    Run(e, f, ref now, 300);
                    e.RequestRelease();
                    Run(e, f, ref now, 200);
                }
                double got = Math.Log(f.K) / Math.Log(1.2);
                Check(Math.Abs(got - 20) < 0.03, "20 corner pinches zoom 1.2^20, got 1.2^" + got.ToString("0.000"));
            }

            {   // slow drags at half speed put fingers on half pixels: they must still round alike (no zoom flicker)
                var f = Chrome(); var e = Make(f); e.PanSpeed = 0.5; double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                double k0 = f.K;
                // the fingers first turn across the drag (whole pixels: the span may change a hair once), then
                // half-pixel steps must round alike: not a flicker
                for (int i = 0; i < 15; i++) { e.AddPan(1, 1); Run(e, f, ref now, 8); }
                double k1 = f.K;
                bool steady = true;
                for (int i = 0; i < 40; i++) { e.AddPan(1, 1); Run(e, f, ref now, 8); if (f.K != k1) steady = false; }
                Check(Math.Abs(k1 / k0 - 1) < 0.002, "turning across the drag barely zooms: x" + (k1 / k0).ToString("0.00000"));
                Check(steady, "zoom doesn't flicker on a half-speed drag");
            }

            {   // a drag at right angles to the fingers: they turn 90 degrees on the way, and the page still moves exactly
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                var u0 = f.Under(1000, 500); double k0 = f.K;
                for (int i = 0; i < 25; i++) { e.AddPan(0, 4); Run(e, f, ref now, 4); }
                Run(e, f, ref now, 40);
                var u1 = f.Under(1000, 500);
                double mx = (u0[0] - u1[0]) * f.K, my = (u0[1] - u1[1]) * f.K;
                Check(Math.Abs(mx) <= 1 && Math.Abs(my - 100) <= 1, "a drag across a 90-degree turn lands exactly: " + mx.ToString("0.00") + "," + my.ToString("0.00"));
            }

            {   // drags must cross the line between the fingers (real Chrome loses up to ~6% of drags along it,
                // tests/phase_test.mjs): the fingers turn across within ~100 ms, and pan re-grips go down across
                foreach (var dir in new[] { new[] { 3, -2 }, new[] { 0, 4 }, new[] { -4, 1 }, new[] { 2, 2 } })
                {
                    var f = Chrome(); var e = Make(f); double now = 0;
                    e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                    for (int i = 0; i < 25; i++) { e.AddPan(dir[0], dir[1]); Run(e, f, ref now, 4); }
                    var m = f.Log[f.Log.Count - 1];
                    double cos = ((m.X1 - m.X0) * dir[0] + (m.Y1 - m.Y0) * dir[1]) / Span(m) / Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1]);
                    Check(Math.Abs(cos) < Math.Sin(6 * Math.PI / 180), "fingers turned across a " + dir[0] + "," + dir[1] + " drag (cos " + cos.ToString("0.000") + ")");
                    // long drag: every later grip goes down across it
                    for (int i = 0; i < 120; i++) { e.AddPan(dir[0] * 10, dir[1] * 10); Run(e, f, ref now, 8); }
                    bool across = true;
                    foreach (var p in f.Log.FindAll(x => x.Kind == "press").GetRange(1, e.Regrips))
                    {
                        double c2 = ((p.X1 - p.X0) * dir[0] + (p.Y1 - p.Y0) * dir[1]) / Span(p) / Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1]);
                        if (Math.Abs(c2) > 0.05) across = false;
                    }
                    Check(e.Regrips > 0 && across, "drag re-grips go down across the drag (" + e.Regrips + ")");
                }
            }

            {   // many gestures that zoom in and back out: whole pixels at each start mustn't walk the page away
                var f = Chrome(); var e = Make(f); double now = 0;
                var before = f.Under(700, 450);
                for (int g = 0; g < 25; g++)
                {
                    e.Engage(700, 450, area, Dpi, 120, now); Run(e, f, ref now, 250);
                    var mid = f.Under(700, 450); double kmid = f.K;
                    e.AddZoom(-120); Run(e, f, ref now, 250);
                    e.RequestRelease(); Run(e, f, ref now, 150);
                    if (Environment.GetEnvironmentVariable("ENGINE_DEBUG") != null)
                        Console.WriteLine("  gesture {0}: zoomed in {1} K {2:0.0000}, out {3} K {4:0.0000}  regrips {5}", g, Fmt(mid), kmid, Fmt(f.Under(700, 450)), f.K, e.Regrips);
                }
                var after = f.Under(700, 450);
                Check(Near(before, after, 2) && Math.Abs(Math.Log(f.K)) < 0.01, "25 in-and-out gestures leave the page where it was: " + Fmt(before) + " -> " + Fmt(after) + " x" + f.K.ToString("0.0000"));
            }

            {   // a very long drag (dozens of re-grips): the zoom must not creep, whole pixels or not
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                double k0 = f.K;
                for (int i = 0; i < 600; i++) { e.AddPan(i % 200 < 100 ? 60 : -60, 9); Run(e, f, ref now, 8); }
                Run(e, f, ref now, 400);
                Check(e.Regrips >= 20, "a long back-and-forth drag re-grips a lot (" + e.Regrips + ")");
                // whole pixels: up to about one pixel of span (~0.4%) at any time, not growing with re-grips
                Check(Math.Abs(f.K / k0 - 1) < 0.005, "no zoom creep over " + e.Regrips + " re-grips: x" + (f.K / k0).ToString("0.00000"));
            }

            {   // a page barely big enough for two fingers: zooming in must not re-grip forever without progress
                var f = Chrome(); var e = Make(f); double now = 0;
                var small = Area(400, 300, 640, 540);
                bool engaged = e.Engage(520, 420, small, Dpi, 120, now);
                for (int i = 0; i < 5; i++) { e.AddZoom(120); Run(e, f, ref now, 50); }
                e.RequestRelease();
                Run(e, f, ref now, 3000);
                Check(!e.Engaged && e.Regrips < 30, "small page: done in time, not re-gripping forever (engaged " + engaged + ", re-grips " + e.Regrips + ")");
            }

            {   // a drag that reverses keeps the fingers' line (across the drag line both ways): no turning back and forth
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                // slightly slanted, so while the drag turns round its averaged direction points sideways for a moment
                for (int i = 0; i < 30; i++) { e.AddPan(6, 1); Run(e, f, ref now, 4); }
                int from = f.Log.Count;
                for (int i = 0; i < 30; i++) { e.AddPan(-6, 1); Run(e, f, ref now, 4); }
                for (int i = 0; i < 30; i++) { e.AddPan(6, 1); Run(e, f, ref now, 4); }
                bool steady = true;
                for (int i = from; i < f.Log.Count; i++)
                {
                    var m = f.Log[i];
                    if (m.Kind == "move" && Math.Abs((m.X1 - m.X0) / Span(m)) > Math.Sin(16 * Math.PI / 180)) steady = false;
                }
                Check(steady, "reversing a drag doesn't turn the fingers");
            }

            {   // a drag going round in circles turns the fingers all the time: their whole pixels mustn't creep the zoom
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 550, area, Dpi, 120, now); Run(e, f, ref now, 300);
                double k0 = f.K;
                for (int i = 0; i < 1200; i++) { double a = i * 2 * Math.PI / 150; e.AddPan(6 * Math.Cos(a), 6 * Math.Sin(a)); Run(e, f, ref now, 4); }
                Run(e, f, ref now, 500);
                Check(Math.Abs(f.K / k0 - 1) < 0.003, "8 circles of dragging keep the zoom: x" + (f.K / k0).ToString("0.00000"));
            }

            {   // a fast drag with a backlog (after re-grips) moves the fingers in steps: big jumps scroll short in Chrome
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                for (int i = 0; i < 60; i++) { e.AddPan(28, 0); Run(e, f, ref now, 4); }
                Run(e, f, ref now, 2000);
                double biggest = 0;
                Ev prev = null;
                foreach (var m in f.Log)
                {
                    if (m.Kind == "press") { prev = null; continue; }
                    if (m.Kind != "move") continue;
                    if (prev != null) biggest = Math.Max(biggest, Dist((m.X0 + m.X1) / 2, (m.Y0 + m.Y1) / 2, (prev.X0 + prev.X1) / 2, (prev.Y0 + prev.Y1) / 2));
                    prev = m;
                }
                Check(e.Regrips > 0 && biggest <= e.MaxStepDip * Dpi + 2, "drag steps stay small, also catching up after re-grips (largest " + biggest.ToString("0.0") + " px)");
            }

            {   // a zoom after a finished drag grips again with the midpoint on the cursor (zooming from off it needs
                // Chrome to scroll, which it gets ~14% wrong once the page itself scrolls)
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                e.AddPan(-200, 80); Run(e, f, ref now, 300);
                int presses = f.Log.FindAll(x => x.Kind == "press").Count;
                e.AddZoom(120); Run(e, f, ref now, 400);
                var later = f.Log.FindAll(x => x.Kind == "press");
                var w = f.Log.Find(x => x.Kind == "move" && x.T > later[later.Count - 1].T);
                Check(later.Count == presses + 1 && w != null && Near(new[] { (Native.Px(w.X0) + Native.Px(w.X1)) / 2.0, (Native.Px(w.Y0) + Native.Px(w.Y1)) / 2.0 }, new[] { 1000.0, 500 }, 1),
                    "a zoom after a drag grips again on the cursor");
            }

            {   // letting go: zoom animation finishes, then the fingers stand still >= HoldMs, then lift
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now);
                e.AddZoom(240);
                Run(e, f, ref now, 12);
                e.RequestRelease();
                Check(!e.CapturesMouse, "after letting go the mouse moves the cursor again");
                Run(e, f, ref now, 1000);
                Check(!e.Engaged && !f.IsDown, "released");
                Check(Math.Abs(f.K / Math.Pow(1.2, 3) - 1) < ZoomTol, "notches queued before letting go still zoom in full: " + f.K);
                int lastMove = f.Log.FindLastIndex(x => x.Kind == "move");
                var rel = f.Log[f.Log.Count - 1];
                Check(rel.Kind == "release" && rel.T - f.Log[lastMove].T >= e.HoldMs, "still for HoldMs before lifting (no fling)");
            }

            {   // momentum: lift right after the last move so Chrome flings
                var f = Chrome(); var e = Make(f); e.Momentum = true; double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                e.AddPan(40, 0); Run(e, f, ref now, 4);
                e.RequestRelease(); Run(e, f, ref now, 8);
                Check(!f.IsDown && f.Log[f.Log.Count - 2].Kind == "move", "momentum lifts right after the last move");
            }

            {   // long drags re-grip and add up exactly
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                var u0 = f.Under(1000, 500);
                int seen = 0;
                for (int i = 0; i < 100; i++)
                {
                    e.AddPan(50, 7); Run(e, f, ref now, 8);
                    if (Environment.GetEnvironmentVariable("ENGINE_DEBUG") != null && e.Regrips != seen)
                    {
                        seen = e.Regrips;
                        var u = f.Under(1000, 500); var sn = e.Snapshot();
                        Console.WriteLine("  regrip {0}: page moved {1:0.0},{2:0.0}  pan given {3:0},{4:0} pending {5:0.0},{6:0.0} K {7:0.0000}",
                            seen, (u0[0] - u[0]) * f.K, (u0[1] - u[1]) * f.K, e.PanTotalX, e.PanTotalY, sn[7], sn[8], f.K);
                    }
                }
                Run(e, f, ref now, 2500);  // drags move at most MaxStepDip a frame: let the backlog drain
                var u1 = f.Under(1000, 500);
                double mx = (u0[0] - u1[0]) * f.K, my = (u0[1] - u1[1]) * f.K;  // screen px the page moved under the cursor
                Check(e.Regrips >= 2, "a 5000 px drag re-grips (" + e.Regrips + ")");
                // each re-grip can leave ~1 px from whole pixels, and the zoom can be off by its rounding (~0.2%)
                double tol = PanTol + 1.5 * e.Regrips + 0.002 * 5000;
                Check(Math.Abs(mx - 5000) < tol && Math.Abs(my - 700) < tol,
                    "drag conserved over " + e.Regrips + " re-grips: " + mx.ToString("0.0") + "," + my.ToString("0.0"));
                Check(!f.Railed, "no axis lock on a shallow diagonal drag");
                Check(Math.Abs(f.K / 1.2 - 1) < ZoomTol, "drag doesn't zoom (re-grips add no zoom): " + f.K.ToString("0.00000"));
            }

            {   // deep zoom in and out re-grips and adds up, anchored at the cursor throughout
                var f = Chrome(); var e = Make(f); double now = 0;
                var before = f.Under(300, 300);
                e.Engage(300, 300, area, Dpi, 120, now);
                for (int i = 0; i < 29; i++) { e.AddZoom(120); Run(e, f, ref now, 30); }
                Run(e, f, ref now, 400);
                Check(e.Regrips >= 2, "deep zoom re-grips (" + e.Regrips + ")");
                Check(Math.Abs(Math.Log(f.K) - 30 * Math.Log(1.2)) < (e.Regrips + 1) * ZoomTol, "30 notches zoom x1.2^30, got 1.2^" + (Math.Log(f.K) / Math.Log(1.2)).ToString("0.00"));
                for (int i = 0; i < 30; i++) { e.AddZoom(-120); Run(e, f, ref now, 30); }
                Run(e, f, ref now, 600);
                // a re-grip's last whole pixels stay until the next zoom repays them: up to ~2 rounding steps here
                Check(Math.Abs(Math.Log(f.K)) < 2 * ZoomTol, "and 30 back is x1 again (leftovers repaid, not summed): " + f.K);
                Check(Near(before, f.Under(300, 300), 2), "deep zoom anchored: " + Fmt(before) + " -> " + Fmt(f.Under(300, 300)));
            }

            {   // random use: fingers stay in the area at Chrome's minimum span or more, no scroll while a pinch starts
                var rnd = new Random(7);
                for (int run = 0; run < 150; run++)
                {
                    var f = Chrome(); var e = Make(f); double now = 0;
                    var a = Area(rnd.Next(0, 400), rnd.Next(0, 300), rnd.Next(900, 2000), rnd.Next(700, 1100));
                    double cx = rnd.Next(a.Left - 50, a.Right + 50), cy = rnd.Next(a.Top - 50, a.Bottom + 50);
                    if (!e.Engage(cx, cy, a, Dpi, rnd.Next(2) == 0 ? 120 : -120, now)) { Check(false, "run " + run + " engage"); continue; }
                    double panX = 0, panY = 0;
                    for (int i = 0; i < 300; i++)
                    {
                        int r = rnd.Next(10);
                        if (r < 3) e.AddZoom(rnd.Next(2) == 0 ? 120 : -120);
                        else if (r < 6) { double dx = rnd.Next(-80, 81), dy = rnd.Next(-80, 81); e.AddPan(dx, dy); panX += dx; panY += dy; }
                        Run(e, f, ref now, rnd.Next(1, 4) * 4);
                    }
                    e.RequestRelease();
                    Run(e, f, ref now, 1500);
                    bool inside = true, spans = true;
                    foreach (var ev in f.Log)
                    {
                        if (ev.X0 < a.Left - 1e-6 || ev.X0 > a.Right - 1 + 1e-6 || ev.Y0 < a.Top - 1e-6 || ev.Y0 > a.Bottom - 1 + 1e-6 ||
                            ev.X1 < a.Left - 1e-6 || ev.X1 > a.Right - 1 + 1e-6 || ev.Y1 < a.Top - 1e-6 || ev.Y1 > a.Bottom - 1 + 1e-6) inside = false;
                        if (Span(ev) < Engine.ChromeMinSpanDip * Dpi) spans = false;
                    }
                    Check(inside, "run " + run + ": fingers stay in the area");
                    Check(spans, "run " + run + ": span never under Chrome's minimum");
                    Check(!f.Railed, "run " + run + ": never locked to an axis");
                    Check(!f.IsDown && !e.Engaged, "run " + run + ": released at the end");
                    Check(e.Failures == 0, "run " + run + ": no failures");
                    Check(Math.Abs(e.PanTotalX - panX) < 1e-6 && Math.Abs(e.PanTotalY - panY) < 1e-6, "run " + run + ": all pan counted");
                }
            }

            {   // mixed use: zoom-only stretches between drags keep the cursor's page point exactly
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(700, 400, area, Dpi, 120, now); Run(e, f, ref now, 300);
                var rnd = new Random(3);
                bool ok = true;
                string bad = "";
                for (int round = 0; round < 20; round++)
                {
                    e.AddPan(rnd.Next(-400, 401), rnd.Next(-300, 301)); Run(e, f, ref now, 200);
                    var p = f.Under(700, 400);
                    for (int i = 0; i < 4; i++) { e.AddZoom(rnd.Next(2) == 0 ? 120 : -120); Run(e, f, ref now, 50); }
                    Run(e, f, ref now, 300);
                    var q = f.Under(700, 400);
                    // whole pixels: up to a pixel can wait to be paid back, plus a pixel of rounding
                    if (!Near(p, q, 2.5)) { ok = false; bad = Fmt(p) + " -> " + Fmt(q); }
                }
                Check(ok, "zoom stays anchored at the cursor between drags " + bad);
            }

            {   // a page too small to pinch on: no fingers at all
                var f = Chrome(); var e = Make(f);
                Check(!e.Engage(150, 150, Area(100, 100, 260, 260), Dpi, 120, 0), "tiny page refused");
                Check(f.Log.Count == 0 && !e.Engaged, "no touch on a tiny page");
            }

            {   // a click lifts at once
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 100);
                e.ReleaseNow();
                Check(!f.IsDown && !e.Engaged && !e.Step(now + 4), "ReleaseNow lifts immediately");
            }

            {   // wheel again while lifting (Ctrl pressed again quickly): the gesture continues
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                e.RequestRelease(); Run(e, f, ref now, 8);
                e.AddZoom(120); Run(e, f, ref now, 400);
                Check(e.Engaged && f.IsDown, "wheel during the lift keeps the gesture");
                Check(Math.Abs(f.K / 1.44 - 1) < 2 * ZoomTol, "both notches zoomed: " + f.K);
            }

            {   // Windows cancels the contacts: put them down again
                var f = Chrome(); var e = Make(f); double now = 0;
                e.Engage(1000, 500, area, Dpi, 120, now); Run(e, f, ref now, 300);
                f.FailMovesAt = f.Log.Count;
                e.AddPan(10, 0); Run(e, f, ref now, 100);
                int presses = f.Log.FindAll(x => x.Kind == "press").Count;
                Check(presses == 2 && e.Engaged && f.IsDown, "cancelled contacts are pressed again");
            }

            {   // fingers only land where HitOk says the page is (another window over part of it)
                var f = Chrome(); var e = Make(f); double now = 0;
                // the best placement straddles the cursor sideways (down at x ~1084): cover the page right of 1050.
                // Only where fingers go down matters: Windows sends a contact's moves to the window it went down on.
                e.HitOk = (x, y) => x <= 1050;
                Check(e.Engage(1000, 500, area, Dpi, 120, now), "engage next to an overlap");
                var p = f.Log[0];
                Check(p.Kind == "press" && p.X0 <= 1050 && p.X1 <= 1050, "fingers go down off the overlap");
            }

            Console.WriteLine("{0} checks, {1} failures", checks, failures);
            return failures;
        }
    }
}
