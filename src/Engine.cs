using System;
using System.Collections.Generic;

namespace ZoomScroller
{
    /// <summary>
    /// Turns wheel notches and mouse motion into one two-finger touch gesture, the way a touchpad pinch reaches Chrome.
    /// Like on a touchpad the cursor stays put: zooming moves both fingers radially from the cursor (Chrome then keeps
    /// the page point under the cursor fixed), and mouse motion moves both fingers together (the page slides under
    /// the cursor). When a finger would leave the page area, or the fingers would get closer than Chrome's minimum,
    /// they are lifted (after standing still, so nothing flings) and put down again: a "re-grip", like lifting your
    /// fingers off a touchpad.
    ///
    /// Chrome's touch handling (ui/events/gesture_detection, cc Viewport; measured on Chrome 155 in
    /// tests/phase_test.mjs and tests/site_test.mjs) shapes the rest:
    /// - a pinch needs the fingers 125 DIP apart and starts once their span changed by 30 DIP (swallowed; the excess
    ///   zooms); it zooms about the fingers' midpoint, and Chrome itself keeps that point still;
    /// - a scroll starts once the midpoint moved 7.2 DIP from where it went down (that much is swallowed), or when a
    ///   pinch starts (then nothing is swallowed). Many pages can't scroll sideways at 100%, so a scroll can't be
    ///   relied on to be undone: the start makes none;
    /// - a big jump of the fingers in one update scrolls short (300 px came out as 223): drags move in steps;
    /// - if finger 0 moves 15 DIP before a pinch is on, Chrome locks scrolls to an axis (rails) until a drag strays
    ///   ~70 px off it; once a pinch is on it decides nothing;
    /// - each finger's update reaches Chrome on its own, so a drag along the line between the fingers briefly changes
    ///   their span, and that costs up to ~6% of the drag; across the line it costs nothing;
    /// - a pinch whose midpoint starts within 100 DIP of the page's edge zooms about the edge (Chrome pins the edge);
    /// - updates that arrive in a burst get merged, and near 100% zoom a zoom-out right after a scroll shifts the page.
    /// So a grip goes down, then (each step on its own) widens past the span slop with finger 0 moving less than the
    /// axis-lock slop and the midpoint ending on the cursor (no scroll, no rails), narrows back about that midpoint
    /// (undoing the excess zoom where it was done), and zooms as a plain pinch about the cursor (a zoom after a drag
    /// grips again first, back on the cursor). Drags keep the fingers turned across them, in steps of at most
    /// MaxStepDip.
    /// </summary>
    public class Engine
    {
        enum State { Idle, Starting, Active, Lifting }

        public const double ChromeMinSpanDip = 125, ChromeSpanSlopDip = 30, ChromeScrollSlopDip = 7.2, ChromeRailSlopDip = 15;
        /// <summary>Past the span slop by this much to be sure it is crossed (the excess zoom is taken back).</summary>
        const double Margin = 2;
        const double TurnStep = 10 * Math.PI / 180, TurnDead = 4 * Math.PI / 180;

        readonly IFingers fingers;
        readonly object gate = new object();

        public double ZoomPerNotch = 1.2;   // page scale factor per 120 wheel units
        public double SmoothMs = 40;        // zoom easing time constant
        public double HoldMs = 110;         // standing still before a lift: Chrome's fling velocity looks back ~100 ms
                                            // (50 ms still flung, 120 didn't: tests/engine_phase_test.mjs long15)
        public double HeartbeatMs = 60;     // re-send still contacts so Windows doesn't cancel them
        public double StartGapMs = 16;      // between the steps of a grip start, so Chrome doesn't merge them
        public double PanSpeed = 1.0;
        /// <summary>Most the fingers move in one update while dragging (DIP); a backlog (after a re-grip) catches up in
        /// steps. Measured: 30 px steps scroll 99.6% of the way, 60 px ~98%, one 300 px jump 74%.</summary>
        public double MaxStepDip = 48;
        public bool InvertPan, Momentum;
        /// <summary>Is this point on the target page? Set per gesture: fingers must land on the same window.</summary>
        public Func<double, double, bool> HitOk = delegate { return true; };

        State state = State.Idle;
        double L, T, R, B;                  // area the fingers stay in (physical px, inclusive)
        double dpi = 1;                     // physical px per DIP
        double cx, cy;                      // the cursor: zoom centre, fixed for the whole gesture
        double ax, ay, bx, by;              // finger 0 and finger 1
        double pendingLog, pendX, pendY;    // zoom (log scale) and pan not applied yet
        bool releaseWanted, finalLift, pinchStarted, dragged;  // dragged: a drag moved the fingers since the grip
        int regripDir, startStage;
        double liftAt, lastStep, lastInject, stageAt;
        double ux, uy;                      // direction from finger 0 to finger 1 at touch-down
        double dirX, dirY;                  // recent drag direction
        double downSpan;                    // touch-down span as Chrome saw it (whole pixels)
        double zoomDebt;                    // zoom Chrome got beyond what was asked (log), from whole pixels; the
                                            // next grip start cancels it, so leftovers don't add up
        double holdSpan;                    // the span Chrome had when the zoom last settled: turns keep to it
        int wax, way, wbx, wby;             // the start's widening, in whole pixels

        public int Regrips { get; private set; }
        public int Failures { get; private set; }
        /// <summary>Pan received over this gesture (after speed/direction), for diagnostics.</summary>
        public double PanTotalX { get; private set; }
        public double PanTotalY { get; private set; }

        public Engine(IFingers fingers) { this.fingers = fingers; }

        public double MinSpan { get { return (ChromeMinSpanDip + 10) * dpi; } }
        public double SlopPx { get { return ChromeSpanSlopDip * dpi; } }
        public double ScrollSlopPx { get { return ChromeScrollSlopDip * dpi; } }
        /// <summary>The start's widening: finger 0 in by a bit less than the axis-lock slop, finger 1 out by the rest
        /// of the span slop (and the margin). The midpoint moves (Out1 - In0) / 2, well under the scroll slop.</summary>
        public double In0 { get { return (ChromeRailSlopDip - 3) * dpi; } }
        public double Out1 { get { return SlopPx + Margin - In0; } }
        /// <summary>How far the midpoint moves while widening; the fingers go down that much short of the cursor.</summary>
        double Lead { get { return (Out1 - In0) / 2; } }

        public bool Engaged { get { lock (gate) return state != State.Idle; } }
        /// <summary>Mouse motion is turned into panning (and swallowed) until a release was asked for.</summary>
        public bool CapturesMouse { get { lock (gate) return state != State.Idle && !releaseWanted; } }

        /// <summary>Puts the fingers down for a gesture starting with this wheel notch. False: no room here.</summary>
        public bool Engage(double x, double y, RECT area, double dpiScale, int wheelDelta, double now)
        {
            lock (gate)
            {
                if (state != State.Idle) { AddZoomLocked(wheelDelta); return true; }
                dpi = dpiScale;
                L = area.Left; T = area.Top; R = area.Right - 1; B = area.Bottom - 1;
                if (R <= L || B <= T) return false;
                cx = Clamp(x, L, R); cy = Clamp(y, T, B);
                pendingLog = 0;
                // a fraction of a pixel the last gesture still owed carries over, so such remainders cancel out
                if (Math.Abs(pendX) >= 1 || Math.Abs(pendY) >= 1) pendX = pendY = 0;
                releaseWanted = false;
                Regrips = 0;
                PanTotalX = PanTotalY = 0;
                dirX = dirY = 0;
                lastStep = now;
                if (!Grip(wheelDelta > 0 ? 1 : -1, now)) return false;
                AddZoomLocked(wheelDelta);
                return true;
            }
        }

        public void AddZoom(int wheelDelta) { lock (gate) AddZoomLocked(wheelDelta); }

        void AddZoomLocked(int wheelDelta)
        {
            if (state == State.Idle) return;
            pendingLog += Math.Log(ZoomPerNotch) * wheelDelta / 120.0;
            releaseWanted = false;
            if (state == State.Lifting && finalLift) { finalLift = false; regripDir = wheelDelta > 0 ? 1 : -1; }
        }

        public void AddPan(double dx, double dy)
        {
            lock (gate)
            {
                if (state == State.Idle || releaseWanted) return;
                double k = InvertPan ? -PanSpeed : PanSpeed;
                pendX += dx * k;
                pendY += dy * k;
                PanTotalX += dx * k;
                PanTotalY += dy * k;
                // the drag's direction, from the mouse itself (pendX/pendY also carry small corrections): an average of
                // unit steps, so its length says how steady the direction is (short while a drag reverses)
                double n = Math.Sqrt(dx * dx + dy * dy);
                if (n > 0) { dirX = dirX * 0.6 + dx * k / n * 0.4; dirY = dirY * 0.6 + dy * k / n * 0.4; }
            }
        }

        /// <summary>Finish the gesture once the zoom animation is done (the user let go of the trigger).</summary>
        public void RequestRelease() { lock (gate) if (state != State.Idle) releaseWanted = true; }

        /// <summary>Lift at once (a click or another key needs the page now).</summary>
        public void ReleaseNow()
        {
            lock (gate)
            {
                if (state == State.Idle) return;
                CountRounding();
                fingers.Release();
                state = State.Idle;
                pendingLog = pendX = pendY = 0;
            }
        }

        /// <summary>One animation frame. Returns false once idle.</summary>
        public bool Step(double now)
        {
            lock (gate)
            {
                if (state == State.Idle) return false;
                double dt = Math.Max(0, now - lastStep);
                lastStep = now;
                switch (state)
                {
                    case State.Starting:
                        if (now - stageAt < StartGapMs) return true;
                        if (startStage == 0)
                        {
                            // 1. widen past the span slop: the pinch starts. Finger 0 moves less than the axis-lock slop
                            // (so Chrome never locks this touch to an axis) and the midpoint less than the scroll slop
                            // (no scroll), ending on the cursor.
                            ax += wax; ay += way; bx += wbx; by += wby;
                        }
                        else
                        {
                            // 2. narrow about the midpoint Chrome has back to its reference span (touch-down span +
                            // slop): the excess zoom is undone about the very point it was done
                            double mx = (Native.Px(ax) + Native.Px(bx)) / 2.0, my = (Native.Px(ay) + Native.Px(by)) / 2.0;
                            double reference = downSpan + SlopPx, h = reference / 2;
                            double[] snap = Snap(mx - ux * h, my - uy * h, mx + ux * h, my + uy * h, reference);
                            if (snap == null) { StartLift(now, false, pendingLog >= 0 ? 1 : -1); return true; }
                            ax = snap[0]; ay = snap[1]; bx = snap[2]; by = snap[3];
                            // whole pixels can shift the midpoint half a pixel, which Chrome scrolls (the pinch is on):
                            // owe it back to the drag, so such halves cancel
                            pendX -= (ax + bx) / 2 - mx; pendY -= (ay + by) / 2 - my;
                            holdSpan = ChromeSpan();
                            pinchStarted = true;
                        }
                        Inject(now);
                        if (++startStage == 2 && state == State.Starting) state = State.Active;
                        stageAt = now;
                        return true;

                    case State.Active:
                    {
                        bool moved = false;
                        if (Math.Abs(pendX) >= 1 || Math.Abs(pendY) >= 1)
                        {
                            moved = Turn();
                            // move by whole pixels (Chrome only sees those; fractions wait in pendX/pendY), as far as
                            // both fingers stay on the page; the rest after a re-grip
                            double t = Math.Min(Fraction(ax, ay), Fraction(bx, by));
                            double want = Math.Sqrt(pendX * pendX + pendY * pendY) * t, cap = MaxStepDip * dpi;
                            double c = want > cap ? cap / want : 1;
                            double mx = Math.Truncate(pendX * t * c), my = Math.Truncate(pendY * t * c);
                            if (c < 1) t = 1;  // capped this frame, not out of room
                            Translate(mx, my);
                            if (mx != 0 || my != 0) dragged = true;
                            pendX -= mx; pendY -= my;
                            moved |= mx != 0 || my != 0;
                            if (t < 1)
                            {
                                if (moved) Inject(now);
                                StartLift(now, false, 0);  // out of room for this drag: grip again in the middle
                                return true;
                            }
                        }
                        if (pendingLog != 0 && dragged && !moved && Math.Abs(pendX) < 1 && Math.Abs(pendY) < 1)
                        {
                            // a zoom starting after a (finished) drag moved the fingers off the cursor: grip again on it,
                            // so the zoom is a plain pinch about the cursor (zooming about it from off it needs Chrome to
                            // scroll, which it does ~14% wrong once the page itself scrolls). While still dragging,
                            // zoom as it is.
                            double fx = (Native.Px(ax) + Native.Px(bx)) / 2.0 - cx, fy = (Native.Px(ay) + Native.Px(by)) / 2.0 - cy;
                            dragged = false;
                            if (fx * fx + fy * fy > 9 * dpi * dpi)
                            {
                                if (moved) Inject(now);
                                StartLift(now, false, pendingLog > 0 ? 1 : -1);
                                return true;
                            }
                        }
                        // a zoom repays what whole pixels left over (zoom Chrome has beyond what was asked)
                        if (pendingLog != 0 && zoomDebt != 0) { pendingLog -= zoomDebt; zoomDebt = 0; }
                        double dl = pendingLog;
                        if (Math.Abs(dl) > 0.003) dl *= 1 - Math.Exp(-dt / SmoothMs);
                        if (dl != 0)
                        {
                            double k = Math.Exp(dl);
                            double nax = cx + k * (ax - cx), nay = cy + k * (ay - cy), nbx = cx + k * (bx - cx), nby = cy + k * (by - cy);
                            if (!(Dist(nax, nay, nbx, nby) >= MinSpan && Inside(nax, nay) && Inside(nbx, nby)))
                            {
                                if (moved) Inject(now);
                                StartLift(now, false, pendingLog > 0 ? 1 : -1);  // out of span: grip again
                                return true;
                            }
                            ax = nax; ay = nay; bx = nbx; by = nby;
                            pendingLog -= dl;
                            moved = true;
                            if (pendingLog == 0) holdSpan = ChromeSpan();

                        }
                        if (moved) Inject(now);
                        else if (now - lastInject >= HeartbeatMs) Hold(now);
                        if (releaseWanted && pendingLog == 0 && Math.Abs(pendX) < 1 && Math.Abs(pendY) < 1 && state == State.Active)
                            StartLift(now, true, 0);
                        return state != State.Idle;
                    }

                    case State.Lifting:
                        // standing still: no updates at all (Chrome then sees no velocity at the lift: no fling)
                        if (now < liftAt) return true;
                        fingers.Release();
                        if (finalLift) { state = State.Idle; return false; }
                        Regrips++;
                        if (!Grip(regripDir, now)) { Failures++; state = State.Idle; return false; }
                        return true;
                }
                return false;
            }
        }

        /// <summary>Turn the fingers about their midpoint toward lying across the drag, a step per frame.</summary>
        bool Turn()
        {
            if (dirX * dirX + dirY * dirY < 0.6 * 0.6) return false;  // no steady direction: don't turn
            double have = Math.Atan2(by - ay, bx - ax), diff = Math.Atan2(dirY, dirX) + Math.PI / 2 - have;
            while (diff > Math.PI / 2) diff -= Math.PI;   // the line between the fingers has no front: either way round
            while (diff <= -Math.PI / 2) diff += Math.PI;
            if (Math.Abs(diff) < TurnDead) return false;
            double step = Clamp(diff, -TurnStep, TurnStep);
            double mx = (ax + bx) / 2, my = (ay + by) / 2, h = Dist(ax, ay, bx, by) / 2;
            for (int tries = 0; tries < 3; tries++, step /= 2)
            {
                double c = Math.Cos(have + step) * h, s = Math.Sin(have + step) * h;
                if (!Inside(mx - c, my - s) || !Inside(mx + c, my + s)) continue;
                double[] snap = Snap(mx - c, my - s, mx + c, my + s, 2 * h, holdSpan);
                if (snap == null) continue;
                double nmx = (snap[0] + snap[2]) / 2, nmy = (snap[1] + snap[3]) / 2;
                ax = snap[0]; ay = snap[1]; bx = snap[2]; by = snap[3];
                // the whole pixels moved the midpoint a little, which Chrome scrolls: take that off the drag
                pendX -= nmx - mx; pendY -= nmy - my;
                return true;
            }
            return false;
        }

        void StartLift(double now, bool final, int dir)
        {
            CountRounding();
            state = State.Lifting;
            finalLift = final;
            regripDir = dir;
            liftAt = now + (final && Momentum ? 0 : HoldMs);
        }

        /// <summary>Lifting leaves Chrome at the rounded span: count the difference from the exact one as debt.</summary>
        void CountRounding()
        {
            if (pinchStarted) zoomDebt += Math.Log(ChromeSpan() / Dist(ax, ay, bx, by));
            pinchStarted = false;
        }

        void Translate(double dx, double dy) { ax += dx; ay += dy; bx += dx; by += dy; }

        static readonly double[][] Directions = BuildDirections();

        static double[][] BuildDirections()
        {
            var d = new double[8][];
            for (int i = 0; i < 8; i++) d[i] = new[] { Math.Cos(i * Math.PI / 4), Math.Sin(i * Math.PI / 4) };
            return d;
        }

        /// <summary>Distance from (px,py) along (dx,dy) to the edge of the area.</summary>
        double Room(double px, double py, double dx, double dy)
        {
            double r = double.MaxValue;
            if (dx > 1e-9) r = Math.Min(r, (R - px) / dx);
            if (dx < -1e-9) r = Math.Min(r, (L - px) / dx);
            if (dy > 1e-9) r = Math.Min(r, (B - py) / dy);
            if (dy < -1e-9) r = Math.Min(r, (T - py) / dy);
            return Math.Max(0, r);
        }

        /// <summary>How far a zoom about the cursor can go before a finger at (px,py) leaves the area (x factor).</summary>
        double Reach(double px, double py)
        {
            double dx = px - cx, dy = py - cy, d = Math.Sqrt(dx * dx + dy * dy);
            return d < 1e-9 ? double.MaxValue : 1 + Room(px, py, dx / d, dy / d) / d;
        }

        struct Placement { public double Score, Mx, My, Ux, Uy, Down; }

        /// <summary>Fingers down. dir +1: zooming in, -1: zooming out (both about the cursor), 0: panning.
        /// A placement is where the fingers' midpoint lands once widened (Mx,My), the direction from finger 0 to
        /// finger 1, and the touch-down span.</summary>
        bool Grip(int dir, double now)
        {
            var options = new List<Placement>();
            double open = In0 + Out1;  // the widening
            if (dir == 0)
            {
                // the middle of the page, lying across the drag (most room for it, and drags across are exact)
                double n = Math.Sqrt(pendX * pendX + pendY * pendY);
                double qx = n >= 1 ? -pendY / n : 0, qy = n >= 1 ? pendX / n : 1;
                double p = MinSpan * 1.5, mx = (L + R) / 2, my = (T + B) / 2;
                options.Add(new Placement { Score = 2, Mx = mx, My = my, Ux = qx, Uy = qy, Down = p });
                options.Add(new Placement { Score = 1, Mx = mx, My = my, Ux = qy, Uy = -qx, Down = p });
            }
            else
            {
                for (int i = 0; i < 8; i++)
                {
                    double qx = Directions[i][0], qy = Directions[i][1];
                    double rPlus = Room(cx, cy, qx, qy) - Margin, rMinus = Room(cx, cy, -qx, -qy) - Margin;
                    // straddling the cursor, midpoint on it: zooming is a plain pinch about the cursor, which Chrome
                    // holds still by itself (best; scored x10)
                    double wide = 2 * Math.Min(rPlus, rMinus) - open;  // widest touch-down span that fits
                    double pS = dir > 0 ? MinSpan : wide;
                    if (pS >= MinSpan && pS <= wide)
                        options.Add(Score(new Placement { Mx = cx, My = cy, Ux = qx, Uy = qy, Down = pS }, dir, 10));
                    // fallback near the page's edge: both on one side, finger 0 next to the cursor (the zoom then needs
                    // Chrome to scroll, which it can't always do)
                    double pR = dir > 0 ? MinSpan : rPlus - open - In0 - 2 * Margin;
                    if (pR >= MinSpan && pR + open + In0 + 2 * Margin <= rPlus)
                    {
                        double m = Margin + In0 + (pR + open) / 2;
                        options.Add(Score(new Placement { Mx = cx + qx * m, My = cy + qy * m, Ux = qx, Uy = qy, Down = pR }, dir, 1));
                    }
                }
                options.RemoveAll(p => p.Score < 1.5);
            }
            options.Sort((p, q) => q.Score.CompareTo(p.Score));
            foreach (var p in options)
            {
                // touch-down on whole pixels, short of the target midpoint by the widening's lead, picked so that the
                // (whole-pixel) widening ends with the midpoint exactly on it; every point on the way must be on the page
                int wax0 = Native.Px(-p.Ux * In0), way0 = Native.Px(-p.Uy * In0), wbx0 = Native.Px(p.Ux * Out1), wby0 = Native.Px(p.Uy * Out1);
                int sx = Native.Px(2 * p.Mx) - wax0 - wbx0, sy = Native.Px(2 * p.My) - way0 - wby0;  // a0 + b0
                int dx = Native.Px(p.Ux * p.Down), dy = Native.Px(p.Uy * p.Down);                    // b0 - a0
                if (((sx - dx) & 1) != 0) dx += dx >= 0 ? 1 : -1;
                if (((sy - dy) & 1) != 0) dy += dy >= 0 ? 1 : -1;
                double a0x = (sx - dx) / 2, a0y = (sy - dy) / 2, b0x = (sx + dx) / 2, b0y = (sy + dy) / 2;
                double s = Dist(a0x, a0y, b0x, b0y), qx = (b0x - a0x) / s, qy = (b0y - a0y) / s;
                if (s < MinSpan - 1) continue;
                if (!(Inside(a0x, a0y) && Inside(b0x, b0y) && Inside(a0x + wax0, a0y + way0) && Inside(b0x + wbx0, b0y + wby0))) continue;
                if (!(HitOk(a0x, a0y) && HitOk(b0x, b0y))) continue;
                if (!fingers.Press(a0x, a0y, b0x, b0y)) { Failures++; return false; }
                ax = a0x; ay = a0y; bx = b0x; by = b0y;
                ux = qx; uy = qy;
                wax = wax0; way = way0; wbx = wbx0; wby = wby0;
                downSpan = ChromeSpan();
                pinchStarted = dragged = false;
                startStage = 0;
                stageAt = now;
                state = State.Starting;
                lastInject = now;
                return true;
            }
            return false;
        }

        /// <summary>Score a zoom placement by the zoom it allows before a re-grip (times a preference factor).</summary>
        Placement Score(Placement p, int dir, double prefer)
        {
            double h = (p.Down + In0 + Out1) / 2;
            double a1x = p.Mx - p.Ux * h, a1y = p.My - p.Uy * h, b1x = p.Mx + p.Ux * h, b1y = p.My + p.Uy * h;
            double range = dir > 0 ? Math.Min(Reach(a1x, a1y), Reach(b1x, b1y)) : 2 * h / MinSpan;
            p.Score = range < 1.5 ? range : range * prefer;
            return p;
        }

        void Inject(double now)
        {
            lastInject = now;
            if (fingers.Move(ax, ay, bx, by)) return;
            Failures++;  // Windows cancelled the contacts: put them down again
            if (!Grip(pendingLog >= 0 ? 1 : -1, now)) state = State.Idle;
        }

        void Hold(double now)
        {
            lastInject = now;
            if (!fingers.Hold()) { Failures++; state = State.Idle; }
        }

        /// <summary>Fraction of the pending pan a finger at (px,py) can make without leaving the area.</summary>
        double Fraction(double px, double py)
        {
            double t = 1;
            if (pendX > 0) t = Math.Min(t, (R - px) / pendX); else if (pendX < 0) t = Math.Min(t, (L - px) / pendX);
            if (pendY > 0) t = Math.Min(t, (B - py) / pendY); else if (pendY < 0) t = Math.Min(t, (T - py) / pendY);
            return Math.Max(0, t);
        }

        /// <summary>Whole-pixel finger positions near these (whole pixels keep Chrome's span through later drags).
        /// keepSpan > 0: the span nearest it (a turn mid-drag must not zoom: turns keep to the span the zoom settled
        /// at, so their roundings don't add up); else the span leaving the least total leftover zoom (debt, against
        /// the span `ideal` stands for). Then the least midpoint shift. The leftover becomes debt. Null: off the page.</summary>
        double[] Snap(double x0, double y0, double x1, double y1, double ideal, double keepSpan = 0)
        {
            double mx = (x0 + x1) / 2, my = (y0 + y1) / 2, bestErr = double.MaxValue, bestShift = 0;
            bool keep = keepSpan > 0;
            double[] best = null;
            foreach (var a0 in new[] { Math.Floor(x0), Math.Ceiling(x0) }) foreach (var a1 in new[] { Math.Floor(y0), Math.Ceiling(y0) })
            foreach (var b0 in new[] { Math.Floor(x1), Math.Ceiling(x1) }) foreach (var b1 in new[] { Math.Floor(y1), Math.Ceiling(y1) })
            {
                if (!Inside(a0, a1) || !Inside(b0, b1)) continue;
                double span = Dist(a0, a1, b0, b1);
                double err = Math.Round(Math.Abs(keep ? Math.Log(span / keepSpan) : zoomDebt + Math.Log(span / ideal)), 9);
                double shift = Math.Abs((a0 + b0) / 2 - mx) + Math.Abs((a1 + b1) / 2 - my);
                if (err < bestErr || (err == bestErr && shift < bestShift)) { bestErr = err; bestShift = shift; best = new[] { a0, a1, b0, b1 }; }
            }
            if (best != null) zoomDebt += Math.Log(Dist(best[0], best[1], best[2], best[3]) / ideal);
            return best;
        }

        /// <summary>The span Chrome computes from the rounded finger positions.</summary>
        double ChromeSpan() { return Dist(Native.Px(ax), Native.Px(ay), Native.Px(bx), Native.Px(by)); }

        bool Inside(double x, double y) { return x >= L - 1e-6 && x <= R + 1e-6 && y >= T - 1e-6 && y <= B + 1e-6; }
        static double Dist(double x0, double y0, double x1, double y1) { return Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); }
        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>Cursor (zoom centre), fingers, pending zoom and pan, for tests and diagnostics.</summary>
        public double[] Snapshot()
        {
            lock (gate) return new[] { cx, cy, ax, ay, bx, by, pendingLog, pendX, pendY };
        }
    }
}
