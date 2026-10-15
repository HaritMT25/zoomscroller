// Experiment (prints, asserts nothing): the real engine (bin\probe.exe engine, scripted wheel/drag input) on real
// Chrome, reading the page point under the cursor between phases. PROBE_LOG=1 prints every touch frame too.
import { spawn } from "node:child_process";
import { join } from "node:path";
import { launch, run, sleep, ROOT } from "./cdp.mjs";

const b = await launch(9465);
const saved = run("drive.exe", ["cursor"]).out.trim().split(/\s+/).map(Number);
try {
  await b.cmd("Page.bringToFront"); await sleep(800);
  const guess = await b.evaluate("({ x: Math.round((screenX + outerWidth / 2) * devicePixelRatio), y: Math.round((screenY + outerHeight - innerHeight / 2) * devicePixelRatio) })");
  const where = run("probe.exe", ["where", "PINCHTEST", guess.x, guess.y]).out;
  const [L, T, R, B] = where.match(/rect (-?\d+) (-?\d+) (-?\d+) (-?\d+)/).slice(1).map(Number);
  const mid = [Math.round((L + R) / 2), Math.round((T + B) / 2)];
  const st = () => b.evaluate("state()");
  const under = (s, x, y) => [s.pl + (x - L) / s.dpr / s.scale, s.pt + (y - T) / s.dpr / s.scale];

  const engine = (x, y, script) => new Promise((resolve, reject) => {
    const p = spawn(join(ROOT, "bin", "probe.exe"), ["engine", "PINCHTEST", x, y, script.join(";")], { env: process.env });
    const reads = [];
    let buf = "";
    p.stdout.on("data", d => {
      buf += d.toString();
      let i;
      while ((i = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, i).trimEnd(); buf = buf.slice(i + 1);
        if (line.startsWith("Q ")) reads.push(sleep(250).then(st).then(s => ({ label: line.slice(2), s })));
        else if (line.trim()) console.log("   " + line.trim());
      }
    });
    p.on("exit", async () => resolve(await Promise.all(reads)));
    p.on("error", reject);
  });
  const reset = async () => {
    for (let i = 0; i < 3 && (await st()).scale > 1.0001; i++) {
      await engine(mid[0], mid[1], ["E -120", ...Array(10).fill("Z -120"), "W 800", "R"]);
      await sleep(300);
    }
    await b.evaluate("scrollTo(0, 1200); 1"); await sleep(700);
  };
  const scroll0 = async () => (await st()).sy;
  const report = async (name, x, y, script) => {
    await reset();
    const s0 = await st();
    const reads = await engine(x, y, script);
    let prev = under(s0, x, y), line = name.padEnd(16);
    for (const r of reads) {
      const u = under(r.s, x, y), k = r.s.scale * r.s.dpr;
      line += `  ${r.label}: ${((prev[0] - u[0]) * k).toFixed(1)},${((prev[1] - u[1]) * k).toFixed(1)} x${r.s.scale.toFixed(3)}`;
      prev = u;
    }
    console.log(line);
  };

  const which = process.argv[2] || "all";
  const cur = [mid[0] - 200, mid[1] - 120];
  if (which === "all" || which === "zoom") await report("zoom 3 notches", ...cur, ["E 120", "W 40", "Z 120", "W 40", "Z 120", "W 400", "Q zoomed", "R", "Q released"]);
  if (which === "gaps") {
    for (const gap of [24, 60, 120, 250]) {
      process.env.START_GAP = String(gap);
      for (let rep = 0; rep < 2; rep++)
        await report(`gap ${gap} ms #${rep}`, ...cur, ["E 120", "W 40", "Z 120", "W 40", "Z 120", "W 600", "Q zoomed", "R", "Q released"]);
    }
  }
  if (which === "all" || which === "drag") await report("drag after zoom", ...cur, ["E 120", "W 300", "Q zoomed", "N 10 -27 -11 12", "W 150", "Q dragged", "Z 120", "W 400", "Q zoomed2", "R", "Q released"]);
  if (which === "all" || which === "long") await report("long drag", ...mid, ["E 120", "W 300", "Q zoomed", "N 25 -60 -16 10", "W 250", "Q dragged", "R", "Q released"]);
  if (which === "long15") {
    for (const [name, script] of [
      ["15deg 1 grip", ["E 120", "W 300", "Q zoomed", "N 6 -60 -16 10", "W 250", "Q dragged", "R", "Q released"]],
      ["15deg 3 grips", ["E 120", "W 300", "Q zoomed", "N 25 -77 -21 10", "W 250", "Q dragged", "R", "Q released"]],
      ["vertical 3 grips", ["E 120", "W 300", "Q zoomed", "N 25 0 -40 10", "W 250", "Q dragged", "R", "Q released"]],
      ["vertical chunks", ["E 120", "W 300", "Q zoomed", "N 8 0 -40 10", "W 300", "Q c1", "N 8 0 -40 10", "W 300", "Q c2", "N 9 0 -40 10", "W 300", "Q c3", "R", "Q released"]],
    ].filter(([n]) => !process.env.ONLY || n.startsWith(process.env.ONLY))) await report(name, ...mid, script);
  }
} finally { b.close(); run("drive.exe", ["setcursor", saved[0], saved[1]]); }
