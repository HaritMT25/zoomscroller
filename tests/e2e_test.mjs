// End to end: a --test instance of MousePinch.exe, tagged SendInput from bin\drive.exe, a throwaway Chrome window.
// Moves your real cursor onto the test window for ~30 s and puts it back at the end.
import { spawn } from "node:child_process";
import { join } from "node:path";
import { launch, run, sleep, ROOT } from "./cdp.mjs";

let failures = 0;
const check = (ok, what, extra = "") => { console.log((ok ? "ok   " : "FAIL ") + what + (extra ? "  " + extra : "")); if (!ok) failures++; };
const drive = steps => { const r = run("drive.exe", [steps]); if (r.code) throw new Error("drive: " + r.out); return r.out; };
const cursor = () => run("drive.exe", ["cursor"]).out.trim().split(/\s+/).map(Number);

const saved = cursor();
// your own mouse moves the same cursor: if it moves during a step, that step's cursor checks are void
console.log("(don't touch the mouse while this runs: about a minute)");
const b = await launch(9462);
const log = [];
let app;
try {
  await b.cmd("Page.bringToFront");
  await sleep(800);
  // page rectangle in physical pixels, from the probe (which also refuses anything but the test window)
  const guess = await b.evaluate("({ x: Math.round((screenX + outerWidth / 2) * devicePixelRatio), y: Math.round((screenY + outerHeight - innerHeight / 2) * devicePixelRatio) })");
  const where = run("probe.exe", ["where", "PINCHTEST", guess.x, guess.y]).out;
  const m = where.match(/rect (-?\d+) (-?\d+) (-?\d+) (-?\d+)/);
  if (!m || /REFUSED/.test(where)) throw new Error("test window not on top: " + where);
  const [L, T, R, B] = m.slice(1).map(Number);
  const cx = Math.round((L + R) / 2), cy = Math.round((T + B) / 2);
  console.log("page rect", L, T, R, B);

  app = spawn(join(ROOT, "bin", "MousePinch.exe"), ["--test", "--seconds", "120"], { stdio: ["ignore", "pipe", "pipe"] });
  app.stdout.on("data", d => log.push(...d.toString().trim().split(/\r?\n/)));
  for (let i = 0; i < 50 && !log.some(l => l.includes("ready")); i++) await sleep(100);
  check(log.some(l => l.includes("ready")), "test instance started");

  const st = () => b.evaluate("state()");
  // page point (CSS px) under a screen point
  const under = (s, x, y) => [s.pl + (x - L) / s.dpr / s.scale, s.pt + (y - T) / s.dpr / s.scale];
  const near = (p, q, tol) => Math.abs(p[0] - q[0]) <= tol && Math.abs(p[1] - q[1]) <= tol;
  const fmt = p => p.map(v => v.toFixed(1)).join(",");
  const lastRelease = async () => { await sleep(100); return log.filter(l => l.includes("released,")).pop() || ""; };
  const panOf = line => (line.match(/pan (-?\d+),(-?\d+)/) || [0, 0, 0]).slice(1).map(Number);
  const reset = async () => { drive(`at ${cx} ${cy}; ctrl down; wheel -1200; sleep 600; ctrl up; sleep 300`); await b.evaluate("scrollTo(0, 0); 1"); await sleep(200); };
  const loadId = (await st()).loadId;

  // 1. zoom in three notches off-centre: x1.2^3 page zoom, browser zoom untouched, point under the cursor fixed
  const ax = cx - 200, ay = cy - 120;
  let s0 = await st();
  drive(`at ${ax} ${ay}; ctrl down; wheel 120; sleep 40; wheel 120; sleep 40; wheel 120; sleep 300; ctrl up; sleep 300`);
  let s1 = await st();
  let c = cursor();
  check(Math.abs(s1.scale / 1.728 - 1) < 0.015, "3 notches zoom the page x1.728", "scale " + s1.scale.toFixed(3));
  check(s1.dpr === s0.dpr, "browser zoom unchanged", "dpr " + s1.dpr);
  check(c[0] === ax && c[1] === ay, "cursor stays where it was", c.join(","));
  check(near(under(s0, ax, ay), under(s1, ax, ay), 1.5), "page point under the cursor stays put", fmt(under(s0, ax, ay)) + " -> " + fmt(under(s1, ax, ay)));

  // 2. Ctrl held: drag slides the page under the still cursor (grab); a zoom after the drag is about the cursor
  await reset();
  drive(`at ${ax} ${ay}; ctrl hold; wheel 120; sleep 300`);
  const sa = await st();
  let steps = "";
  for (let i = 0; i < 10; i++) steps += `move -15 -6; sleep 12; `;
  drive(steps + "sleep 150");
  const sb = await st();
  drive("wheel 120; sleep 350");
  const sc = await st();
  c = cursor();
  drive("ctrl up; sleep 300");
  const rel2 = await lastRelease();
  const [px, py] = panOf(rel2);
  console.log("      ", rel2.replace(/^.*released/, "released"));
  const pa = under(sa, ax, ay), pb = under(sb, ax, ay), pc = under(sc, ax, ay);
  const want = [pa[0] - px / sa.dpr / sa.scale, pa[1] - py / sa.dpr / sa.scale];
  check(px < -100 && py < -30, "the mouse motion reached the gesture", `pan ${px},${py}`);
  check(c[0] === ax && c[1] === ay, "cursor stays still while dragging", c.join(","));
  check(Math.abs(sb.scale - sa.scale) < 0.002, "dragging doesn't zoom", sa.scale.toFixed(3) + " -> " + sb.scale.toFixed(3));
  check(near(pb, want, 3), "the page moved with the mouse", "page under cursor " + fmt(pa) + " -> " + fmt(pb) + ", expected " + fmt(want));
  check(Math.abs(sc.scale / sb.scale / 1.2 - 1) < 0.015, "the next notch zooms x1.2", (sc.scale / sb.scale).toFixed(3));
  check(near(pb, pc, 1.5), "zoom after the drag is about the cursor", fmt(pb) + " -> " + fmt(pc));

  // 3. a long drag re-grips and keeps going, without zoom creep
  await reset();
  await b.evaluate("scrollTo(0, 1200); 1");  // room on the right and below for the whole drag
  await sleep(200);
  drive(`at ${cx} ${cy}; ctrl hold; wheel 120; sleep 300`);
  const la = await st();
  steps = "";
  for (let i = 0; i < 25; i++) steps += `move -30 -8; sleep 10; `;
  drive(steps + "sleep 250");
  const lb = await st();
  drive("ctrl up; sleep 300");
  const rel3 = await lastRelease();
  const [lx, ly] = panOf(rel3);
  const regrips = Number((rel3.match(/regrips (\d+)/) || [])[1]);
  const movedX = -(lb.pl - la.pl) * la.dpr * la.scale, movedY = -(lb.pt - la.pt) * la.dpr * la.scale;
  console.log(`       long drag: pan ${lx},${ly} regrips ${regrips} page moved ${movedX.toFixed(0)},${movedY.toFixed(0)} px`);
  check(regrips >= 1, "long drag re-gripped");
  // each grip may leave a pixel or two (whole pixels), and the drag is converted at one zoom: allow 3 px a grip + 1%
  const allow = 3 * (regrips + 1) + 0.01 * Math.hypot(lx, ly);
  check(Math.abs(movedX - lx) < allow && Math.abs(movedY - ly) < allow, "page followed the whole drag across re-grips",
    `missing ${(lx - movedX).toFixed(0)},${(ly - movedY).toFixed(0)} px (allowed ${allow})`);
  check(Math.abs(lb.scale / la.scale - 1) < 0.005, "no zoom creep from re-grips", la.scale.toFixed(4) + " -> " + lb.scale.toFixed(4));

  // 4. zoom all the way out: stops at exactly 100%
  steps = `at ${cx} ${cy}; ctrl down`;
  for (let i = 0; i < 12; i++) steps += `; wheel -120; sleep 30`;
  drive(steps + `; sleep 400; ctrl up; sleep 300`);
  s1 = await st();
  check(s1.scale === 1, "zooming out stops at 100%", "scale " + s1.scale);
  check(s1.dpr === s0.dpr, "browser zoom still unchanged");

  // 5. without Ctrl the wheel is a plain scroll (Chrome ignores the mouse for ~0.5 s after touch: wait first)
  await b.evaluate("scrollTo(0, 0); 1");
  await sleep(600);
  s0 = await st();
  drive(`at ${cx} ${cy}; wheel -120; sleep 400`);
  s1 = await st();
  check(s1.sy > s0.sy && s1.scale === 1, "plain wheel scrolls normally", `scrollY ${s0.sy} -> ${s1.sy}`);
  drive(`wheel 120; sleep 300`);

  // 5b. the plain wheel right after letting go of Ctrl (Chrome ignores mouse input for a while after touch)
  // (zoom in and back out, so only the wheel can move the page)
  for (const gap of [0, 200, 400, 600, 800]) {
    await b.evaluate("scrollTo(0, 400); 1"); await sleep(700);
    s0 = await st();
    drive(`at ${cx} ${cy}; ctrl down; wheel 120; sleep 250; wheel -120; sleep 350; ctrl up; sleep ${gap}; wheel -120; sleep 400`);
    s1 = await st();
    console.log(`       plain wheel ${gap} ms after letting go of Ctrl: ${s1.sy > s0.sy ? "scrolled" : "LOST"} (scale ${s1.scale.toFixed(3)})`);
  }

  // 6. clicks: how soon after a gesture does Chrome take a mouse click? (it may ignore mouse right after touch)
  for (const gap of [0, 50, 150, 400]) {
    s0 = await st();
    drive(`at ${cx} ${cy}; ctrl down; wheel 120; sleep 300; ctrl up; sleep ${100 + gap}; click; sleep 200`);
    s1 = await st();
    console.log(`       click ${gap} ms after the lift: ${s1.clicks - s0.clicks ? "reached the page" : "LOST"}`);
  }
  s0 = await st();
  drive(`at ${cx} ${cy}; ctrl down; wheel 120; sleep 300; click; sleep 300; ctrl up; sleep 300`);
  s1 = await st();
  await sleep(100);
  check(log.some(l => l.includes("released for a click")), "a click while pinching lifts the fingers");
  console.log(`       click during the gesture: ${s1.clicks - s0.clicks ? "reached the page" : "LOST"}`);
  const nRel = log.filter(l => l.includes("released,")).length;
  drive(`ctrl hold; wheel 120; sleep 150; key 43; sleep 400`);
  await sleep(100);
  check(log.filter(l => l.includes("released,")).length === nRel + 1, "another key while Ctrl is held ends the gesture");
  drive(`ctrl up; sleep 100`);
  const clicksSoFar = (await st()).clicks;

  // 7. dragging hard into the page edges neither navigates back nor reloads
  await reset();
  await b.evaluate("history.pushState({}, '', '?second'); 1");
  steps = `at ${cx} ${cy}; ctrl down; wheel 120; wheel 120; sleep 250`;
  for (let i = 0; i < 40; i++) steps += `; move 40 30; sleep 8`;
  drive(steps + `; sleep 300; ctrl up; sleep 1200`);
  s1 = await st();
  check(s1.url.endsWith("?second"), "no back navigation from dragging into the left edge", s1.url);
  check(s1.loadId === loadId, "no reload from dragging into the top edge");
  check(s1.ctx === 0, "no context menu ever");
  check(s1.clicks === clicksSoFar, "no stray clicks from gestures", "clicks " + s1.clicks);
  const fails = log.filter(l => /failures [1-9]|frames:|hook:|no room/.test(l));
  check(fails.length === 0, "no injection failures", fails.join(" | "));
} catch (e) {
  failures++;
  console.log("FAIL", e.message);
} finally {
  try { run("drive.exe", ["ctrl up"]); } catch { }
  if (app) app.kill();
  b.close();
  run("drive.exe", ["setcursor", saved[0], saved[1]]);
  console.log("--- app log ---\n" + log.join("\n"));
  console.log(failures ? `${failures} FAILED` : "all passed");
  process.exitCode = failures ? 1 : 0;
}
