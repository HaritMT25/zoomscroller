// Experiment (prints, asserts nothing): the real engine zooming on a real site in a throwaway Chrome profile.
//   node tests/site_test.mjs [url]       (default https://www.youtube.com/)
// At several points: what touch-action the page has there, and what 3 notches did (zoom, drift under the cursor).
import { spawn } from "node:child_process";
import { join } from "node:path";
import { launch, run, sleep, ROOT } from "./cdp.mjs";

const url = process.argv.find(a => /^(https?|file):/.test(a)) || "https://www.youtube.com/";
const withCss = process.argv.includes("--css");  // what the helper extension does (extension/touch.css)
const b = await launch(9468, [], url);
const marker = "pid:" + b.pid;
try {
  for (let i = 0; i < 100 && (await b.evaluate("document.readyState")) !== "complete"; i++) await sleep(200);
  await sleep(4000);  // let the site settle (grids, lazy content)
  if (withCss) {
    const css = (await import("node:fs")).readFileSync(join(ROOT, "extension", "touch.css"), "utf8");
    await b.evaluate(`(() => { const s = document.createElement("style"); s.textContent = ${JSON.stringify(css)}; document.documentElement.appendChild(s); return 1; })()`);
    console.log("(with the helper's CSS)");
  }
  await b.cmd("Page.bringToFront"); await sleep(500);
  const found = run("probe.exe", ["find", marker]).out;
  const fm = found.match(/page (-?\d+) (-?\d+) (-?\d+) (-?\d+)/);
  if (!fm) throw new Error("test window not found: " + found);
  const [L, T, R, B] = fm.slice(1).map(Number);
  await sleep(300);
  const where = run("probe.exe", ["where", marker, Math.round((L + R) / 2), Math.round((T + B) / 2)]).out;
  if (/REFUSED/.test(where)) throw new Error("test window not on top: " + where);
  const vv = () => b.evaluate("({ scale: visualViewport.scale, pl: visualViewport.pageLeft, pt: visualViewport.pageTop, dpr: devicePixelRatio, sx: scrollX, sy: scrollY })");
  const under = (s, x, y) => [s.pl + (x - L) / s.dpr / s.scale, s.pt + (y - T) / s.dpr / s.scale];
  const info = (x, y) => b.evaluate(`(() => { const d = devicePixelRatio, e = document.elementFromPoint(${x - L} / d, ${y - T} / d);
      const chain = []; for (let n = e; n && chain.length < 40; n = n.parentElement) { const t = getComputedStyle(n).touchAction; if (t !== "auto") chain.push(n.tagName.toLowerCase() + (n.id ? "#" + n.id : "") + ": " + t); }
      return (e ? e.tagName.toLowerCase() + (e.id ? "#" + e.id : "") : "none") + (chain.length ? "  touch-action " + chain.join(", ") : "  touch-action auto"); })()`);
  const engine = (x, y, script) => new Promise(res => {
    const p = spawn(join(ROOT, "bin", "probe.exe"), ["engine", marker, x, y, script.join(";")]);
    const reads = []; let buf = "", out = [];
    p.stdout.on("data", d => { buf += d; let i; while ((i = buf.indexOf("\n")) >= 0) { const l = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
      if (l.startsWith("Q ")) reads.push(sleep(250).then(vv).then(s => ({ label: l.slice(2), s }))); else if (l) out.push(l); } });
    p.on("exit", async () => res({ reads: await Promise.all(reads), out }));
  });
  const W = R - L, H = B - T;
  if (process.argv.includes("--fresh")) {
    // zoom with the engine, let go; then a NEW touch drags the zoomed page up: does the page itself scroll?
    const frames = f => new Promise(res => {
      const p = spawn(join(ROOT, "bin", "probe.exe"), ["frames", marker, Math.round(L + W / 2), Math.round(T + H / 2), f.join(";")]);
      const reads = []; let buf = "";
      p.stdout.on("data", d => { buf += d; let i; while ((i = buf.indexOf(String.fromCharCode(10))) >= 0) { const l = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
        if (l.startsWith("Q ")) reads.push(sleep(250).then(vv).then(s => ({ label: l.slice(2), s }))); } });
      p.on("exit", async () => res(await Promise.all(reads)));
    });
    const x = Math.round(L + W * 0.6), y = Math.round(T + H * 0.6);
    const kinds = {
      "one finger": Array.from({ length: 30 }, (_, i) => `o ${x} ${y - 15 * (i + 1)}`),
      "two fingers": Array.from({ length: 30 }, (_, i) => `M ${x - 90} ${y - 15 * (i + 1)} ${x + 90} ${y - 15 * (i + 1)}`),
    };
    for (const [name, moves] of Object.entries(kinds)) {
      await engine(x, y, ["E 120", "W 40", "Z 120", "W 40", "Z 120", "W 600", "R"]);
      await sleep(400);
      const z = await vv();
      const start = name === "one finger" ? [`O ${x} ${y}`] : [`P ${x - 90} ${y} ${x + 90} ${y}`];
      const end = name === "one finger" ? ["H 6", "u"] : ["H 6", "U"];
      const reads = await frames([...start, ...moves, "Q dragged", ...end, "Q lifted"]);
      const d = reads.find(r => r.label === "dragged");
      const u0 = under(z, x, y), u1 = under(d.s, x, y), k = d.s.scale * d.s.dpr;
      console.log(`${name.padEnd(12)} x${z.scale.toFixed(3)}  drag up 450: page moved ${((u0[0] - u1[0]) * k).toFixed(0)},${((u0[1] - u1[1]) * k).toFixed(0)}  scrollY ${z.sy.toFixed(0)} -> ${d.s.sy.toFixed(0)}`);
      for (let i = 0; i < 3 && (await vv()).scale > 1.0001; i++) await engine(Math.round(L + W / 2), Math.round(T + H / 2), ["E -120", ...Array(12).fill("Z -120"), "W 900", "R"]);
      await b.evaluate("scrollTo(0, 0); 1"); await sleep(500);
    }
    throw new Error("done");
  }
  if (process.argv.includes("--drag")) {
    // zoom where the cursor is, then (Ctrl still held) drag the page up and down, like two fingers on a touchpad
    for (const [name, fx, fy] of [["grid middle", 0.6, 0.5], ["grid upper left", 0.35, 0.3]]) {
      const x = Math.round(L + W * fx), y = Math.round(T + H * fy);
      const s0 = await vv();
      await b.evaluate("scrollTo(0, 1500); 1"); await sleep(600);
      const { reads, out } = await engine(x, y, ["E 120", "W 40", "Z 120", "W 40", "Z 120", "W 600", "Q zoomed",
        "N 40 0 -20 12", "W 200", "Q up", "N 80 0 20 12", "W 200", "Q down", "R", "Q released"]);
      const g = l => reads.find(r => r.label === l);
      if (!g("zoomed") || !g("down")) { console.log(name, "no reading", out.join(" | ")); continue; }
      const moved = (p, q) => { const u = under(g(p).s, x, y), v = under(g(q).s, x, y), k = g(q).s.scale * g(q).s.dpr; return [(u[0] - v[0]) * k, (u[1] - v[1]) * k]; };
      const up = moved("zoomed", "up"), down = moved("up", "down");
      console.log(`${name.padEnd(16)} x${g("zoomed").s.scale.toFixed(3)}  drag up 800: page moved ${up.map(v => v.toFixed(0))}  then down 1600: ${down.map(v => v.toFixed(0))}  (scrollY ${g("zoomed").s.sy.toFixed(0)} -> ${g("up").s.sy.toFixed(0)} -> ${g("down").s.sy.toFixed(0)})  ${out.filter(l => l.startsWith("inject")).join(" ")}`);
      for (let i = 0; i < 3 && (await vv()).scale > 1.0001; i++) await engine(Math.round(L + W / 2), Math.round(T + H / 2), ["E -120", ...Array(12).fill("Z -120"), "W 900", "R"]);
      await b.evaluate("scrollTo(0, 0); 1"); await sleep(500);
    }
    throw new Error("done");
  }
  const points = [["grid middle", 0.6, 0.5], ["grid upper left", 0.35, 0.3], ["near top (chips/header)", 0.6, 0.12], ["left side (guide)", 0.06, 0.5], ["grid lower right", 0.85, 0.85]];
  for (const [name, fx, fy] of points) {
    const x = Math.round(L + W * fx), y = Math.round(T + H * fy);
    const s0 = await vv();
    const { reads, out } = await engine(x, y, ["E 120", "W 40", "Z 120", "W 40", "Z 120", "W 600", "Q zoomed", "R", "Q released"]);
    const z = reads.find(r => r.label === "zoomed");
    const what = await info(x, y);
    if (!z) { console.log(`${name.padEnd(24)} no reading (${out.join(" | ")})  at ${what}`); continue; }
    const u0 = under(s0, x, y), u1 = under(z.s, x, y), k = z.s.scale * z.s.dpr;
    console.log(`${name.padEnd(24)} zoom x${z.s.scale.toFixed(3)}  drift ${((u0[0] - u1[0]) * k).toFixed(1)},${((u0[1] - u1[1]) * k).toFixed(1)} px  ${out.filter(l => l.startsWith("inject")).join(" ")}\n${" ".repeat(26)}at ${what}`);
    // back to x1
    for (let i = 0; i < 3 && (await vv()).scale > 1.0001; i++) await engine(Math.round(L + W / 2), Math.round(T + H / 2), ["E -120", ...Array(12).fill("Z -120"), "W 900", "R"]);
    await sleep(400);
  }
} catch (e) { if (e.message !== "done") console.log("FAIL", e.message); } finally { b.close(); }
