// Experiment (prints, asserts nothing): the engine's touch sequence one phase at a time on real Chrome, reading the
// page point under the cursor between phases while the fingers stay down. Where does content move unasked, and
// which finger motions make Chrome land exactly where the fingers went?
//   node tests/phase_test.mjs [endings|starts]
import { spawn } from "node:child_process";
import { join } from "node:path";
import { launch, run, sleep, ROOT } from "./cdp.mjs";

const which = process.argv[2] || "endings";
const b = await launch(9464);
const saved = run("drive.exe", ["cursor"]).out.trim().split(/\s+/).map(Number);
try {
  await b.cmd("Page.bringToFront"); await sleep(800);
  const guess = await b.evaluate("({ x: Math.round((screenX + outerWidth / 2) * devicePixelRatio), y: Math.round((screenY + outerHeight - innerHeight / 2) * devicePixelRatio) })");
  const where = run("probe.exe", ["where", "PINCHTEST", guess.x, guess.y]).out;
  const [L, T, R, B] = where.match(/rect (-?\d+) (-?\d+) (-?\d+) (-?\d+)/).slice(1).map(Number);
  const cx = Math.round((L + R) / 2) - 100, cy = Math.round((T + B) / 2) - 60;  // the "cursor", off-centre
  const st = () => b.evaluate("state()");
  const under = s => [s.pl + (cx - L) / s.dpr / s.scale, s.pt + (cy - T) / s.dpr / s.scale];

  // run a frame script; at each Q read the page
  const phases = script => new Promise((resolve, reject) => {
    const p = spawn(join(ROOT, "bin", "probe.exe"), ["frames", "PINCHTEST", cx, cy, script.join(";")]);
    const reads = [];
    let buf = "";
    p.stdout.on("data", d => {
      buf += d.toString();
      let i;
      while ((i = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
        if (line.startsWith("Q ")) reads.push(sleep(250).then(st).then(s => ({ label: line.slice(2), s })));
        else if ((line.startsWith("inject") && !line.includes("ok=True")) || line.startsWith("REFUSED") || line.startsWith("hit ") && !line.includes("PINCHTEST")) console.log("   PROBE:", line);
      }
    });
    p.on("exit", async () => resolve(await Promise.all(reads)));
    p.on("error", reject);
  });

  const reset = async () => {
    const sym = s => `${cx - s / 2} ${cy} ${cx + s / 2} ${cy}`;
    for (let i = 0; i < 3 && (await st()).scale > 1.0001; i++) {
      const r = run("probe.exe", ["frames", "PINCHTEST", cx, cy, ["P " + sym(500), ...[440, 380, 320, 260, 200, 160].map(s => "M " + sym(s)), "H 6", "U"].join(";")]);
      if (!/ok=True/.test(r.out)) console.log("   RESET PROBE:", r.out.trim().replace(/\s*\n\s*/g, " | "));
      await sleep(400);
    }
    if ((await st()).scale > 1.0001) console.log("   RESET FAILED, scale", (await st()).scale);
    await b.evaluate("scrollTo(500, 1200); 1"); await sleep(700);
  };

  // fingers straddle the cursor sideways; optional kick; pinch start; x1.2 about the cursor; then a drag
  const sequence = ({ kick = null, back = null, axis = [1, 0], drag, steps = 10, ending = "hold", order = "normal", widen = true, quiet = false }) => {
    const h = 84.5;
    let a = [cx - axis[0] * h, cy - axis[1] * h], c = [cx + axis[0] * h, cy + axis[1] * h];
    const f = [`P ${a[0]} ${a[1]} ${c[0]} ${c[1]}`];
    let mv = (dx, dy) => { a = [a[0] + dx, a[1] + dy]; c = [c[0] + dx, c[1] + dy]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`); };
    const spread = d => { a = [a[0] - axis[0] * d, a[1] - axis[1] * d]; c = [c[0] + axis[0] * d, c[1] + axis[1] * d]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`); };
    if (kick && kick.length === 2) { mv(15 * kick[0], 15 * kick[1]); mv(-2 * kick[0], -2 * kick[1]); }
    if (kick && kick.length === 3) { mv(kick[0], kick[1]); if (!quiet) f.push("Q kicked"); if (back) mv(back[0], back[1]); }
    if (widen) { spread(9.875); spread(9.875); spread(-1); }
    for (let i = 0; i < 10; i++) {
      const k = Math.pow(1.2, 0.1);
      a = [cx + k * (a[0] - cx), cy + k * (a[1] - cy)]; c = [cx + k * (c[0] - cx), cy + k * (c[1] - cy)];
      f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`);
    }
    f.push("Q zoomed");
    if (order === "alternate") f.push("A");
    const plain = mv;
    if (order === "reversed") mv = (dx, dy) => { a = [a[0] + dx, a[1] + dy]; c = [c[0] + dx, c[1] + dy]; f.push(`N ${a[0]} ${a[1]} ${c[0]} ${c[1]}`); };
    if (order === "zy" || order === "z" || order === "y") {
      // whole steps, one finger at a time; zy alternates which finger goes first
      for (let i = 0; i < steps; i++) {
        const cmd = order === "zy" ? (i % 2 ? "Y" : "Z") : order.toUpperCase();
        f.push(`${cmd} ${drag[0] / steps} ${drag[1] / steps}`);
      }
      a = [a[0] + drag[0], a[1] + drag[1]]; c = [c[0] + drag[0], c[1] + drag[1]];
    } else if (ending === "rapid") {
      // each frame: 1-px moves back to back (whole pixels, the remainder in the last frame)
      const n = Math.max(Math.abs(drag[0]), Math.abs(drag[1]));
      const per = Math.ceil(n / steps);
      let done = 0;
      while (done < n) {
        const k = Math.min(per, n - done);
        f.push(`F ${k} ${drag[0] / n} ${drag[1] / n}`);
        done += k;
      }
      a = [a[0] + drag[0], a[1] + drag[1]]; c = [c[0] + drag[0], c[1] + drag[1]];
    } else if (ending === "ease") {
      // exponential approach: each frame covers 35% of what is left, then the last fraction at once
      let left = [drag[0], drag[1]];
      for (let i = 0; i < 14; i++) { const s = [left[0] * 0.35, left[1] * 0.35]; mv(s[0], s[1]); left = [left[0] - s[0], left[1] - s[1]]; }
      mv(left[0], left[1]);
    } else {
      for (let i = 0; i < steps; i++) mv(drag[0] / steps, drag[1] / steps);
    }
    if (ending === "wiggle") { f.push("S 40"); mv(1, 0); mv(-1, 0); }
    if (ending === "wiggle-late") { f.push("H 10"); mv(1, 0); mv(-1, 0); }
    if (ending === "repeat") { for (let i = 0; i < 6; i++) { mv(0.4, 0); mv(-0.4, 0); } }
    f.push("Q dragged", "H 6", "U", "Q lifted");
    return f;
  };

  const report = async (name, opts) => {
    await reset();
    const reads = await phases(sequence(opts));
    const z = reads.find(r => r.label === "zoomed"), d = reads.find(r => r.label === "dragged"), l = reads.find(r => r.label === "lifted");
    if (!z || !d || !l) { console.log(name, "missing reads"); return; }
    const k = z.s.scale * z.s.dpr, uz = under(z.s), ud = under(d.s), ul = under(l.s);
    const got = [(uz[0] - ud[0]) * k, (uz[1] - ud[1]) * k], after = [(ud[0] - ul[0]) * k, (ud[1] - ul[1]) * k];
    const pct = (g, w) => w ? (100 * g / w).toFixed(0) + "%" : (g.toFixed(1) + "px");
    const dz = d.s.scale / z.s.scale;
    console.log(`${name.padEnd(34)} drag ${opts.drag}: moved ${got.map(v => v.toFixed(1))}  (${pct(got[0], opts.drag[0])}, ${pct(got[1], opts.drag[1])})  at lift ${after.map(v => v.toFixed(1))}${Math.abs(dz - 1) > 0.0005 ? "  ZOOM x" + dz.toFixed(4) : ""}`);
    if (process.env.VIEWPORTS) {
      const f = s => `visual ${s.vx.toFixed(1)},${s.vy.toFixed(1)} layout ${s.sx.toFixed(1)},${s.sy.toFixed(1)}`;
      const lw = await b.evaluate("({ w: innerWidth, h: innerHeight, vw: visualViewport.width, vh: visualViewport.height, sw: document.documentElement.scrollWidth })");
      console.log(`      zoomed ${f(z.s)} | dragged ${f(d.s)} | layout ${lw.w}x${lw.h} visual ${lw.vw.toFixed(1)}x${lw.vh.toFixed(1)} doc ${lw.sw}`);
    }
  };

  if (which === "slop") {
    // a kick with no step back, fingers down sideways at the cursor: how much of it does Chrome swallow?
    for (const k of [[15, 15], [25, 25], [40, 40], [30, 0], [0, 30], [60, 0]]) {
      await reset();
      const f = [`P ${cx - 84} ${cy} ${cx + 85} ${cy}`, "Q down", `M ${cx - 84 + k[0]} ${cy + k[1]} ${cx + 85 + k[0]} ${cy + k[1]}`, "Q kicked", "H 6", "U", "Q lifted"];
      const reads = await phases(f);
      const r0 = reads.find(r => r.label === "down"), r1 = reads.find(r => r.label === "kicked");
      const kk = r0.s.scale * r0.s.dpr, u0 = under(r0.s), u1 = under(r1.s);
      const m = [(u0[0] - u1[0]) * kk, (u0[1] - u1[1]) * kk];
      const len = Math.hypot(k[0], k[1]), got = Math.hypot(m[0], m[1]);
      console.log(`kick ${k}: page moved ${m.map(v => v.toFixed(1))}  swallowed ${(len - got).toFixed(1)} px of ${len.toFixed(1)}`);
    }
  } else if (which === "fix") {
    const drags = [[-200, -80], [200, -80], [-200, 80], [200, 80], [-80, -200], [80, -200], [-80, 200], [80, 200], [-200, 0], [0, 200]];
    for (const [name, k, back, order] of [["kick 25 + back, zy", [25, 25, 0], [-18, -18], "zy"], ["kick 40 + back, zy", [40, 40, 0], [-33, -33], "zy"],
                                          ["kick 40 + back, plain", [40, 40, 0], [-33, -33], "normal"]]) {
      for (const drag of drags) {
        await report(name, { kick: k, back, drag, order, quiet: true });
      }
    }
    // net shift of kick + back + pinch start alone
    for (const [k, back] of [[[25, 25, 0], [-18, -18]], [[40, 40, 0], [-33, -33]], [[40, 40, 0], [-34, -33]]]) {
      await reset();
      const reads = await phases(["P " + (cx - 84) + " " + cy + " " + (cx + 85) + " " + cy, "Q down", ...sequence({ kick: k, back, drag: [0, 0], steps: 1, quiet: true }).slice(1)]);
      const r0 = reads.find(r => r.label === "down"), r1 = reads.find(r => r.label === "zoomed");
      const u0 = under(r0.s), u1 = under(r1.s);
      console.log(`kick ${k.slice(0, 2)} back ${back}: point under the cursor moved ${((u0[0] - u1[0]) * r1.s.scale * r1.s.dpr).toFixed(1)},${((u0[1] - u1[1]) * r1.s.scale * r1.s.dpr).toFixed(1)} px through the zoom`);
    }
  } else if (which === "fix2") {
    const drags = [[-200, -80], [200, -80], [-200, 80], [200, 80], [-80, -200], [80, -200], [-80, 200], [80, 200], [-200, 0], [0, 200]];
    for (const [name, order, perp] of [["sideways zy", "zy", false], ["perpendicular plain", "normal", true], ["perpendicular zy", "zy", true]])
      for (const drag of drags) {
        const n = Math.hypot(drag[0], drag[1]);
        const axis = perp ? [-drag[1] / n, drag[0] / n] : [1, 0];
        await report(name, { kick: [40, 40, 0], back: [-33, -33], axis, drag, order, quiet: true });
      }
  } else if (which === "v3") {
    // grip start: press, finger 1 alone out past the span slop (finger 0 never moves before the pinch is on, so no
    // axis lock is ever decided), then both back by the scroll Chrome made of it: (W - slop) / 2 along the axis
    const SLOP = 9.4, W = 39.5;
    const seq = (axis, drag, rotate) => {
      let a = [cx - axis[0] * 84.5, cy - axis[1] * 84.5], c = [cx + axis[0] * 84.5, cy + axis[1] * 84.5];
      a = a.map(Math.round); c = c.map(Math.round);
      const f = [`P ${a[0]} ${a[1]} ${c[0]} ${c[1]}`, "Q down", "S 8"];
      const put = () => f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`);
      c = [c[0] + axis[0] * W, c[1] + axis[1] * W]; put(); f.push("S 8");
      const back = (W - SLOP) / 2;
      a = [a[0] - axis[0] * back, a[1] - axis[1] * back]; c = [c[0] - axis[0] * back, c[1] - axis[1] * back]; put();
      f.push("Q started");
      for (let i = 0; i < 10; i++) {  // x1.2 about the cursor
        const k = Math.pow(1.2, 0.1);
        a = [cx + k * (a[0] - cx), cy + k * (a[1] - cy)]; c = [cx + k * (c[0] - cx), cy + k * (c[1] - cy)]; put();
      }
      f.push("Q zoomed");
      if (rotate) {  // turn the pair about its midpoint to lie across the drag, 10 degrees a frame
        const n = Math.hypot(drag[0], drag[1]), want = Math.atan2(drag[0] / n, -drag[1] / n);
        const m = [(a[0] + c[0]) / 2, (a[1] + c[1]) / 2], h = Math.hypot(c[0] - a[0], c[1] - a[1]) / 2;
        let ang = Math.atan2(c[1] - a[1], c[0] - a[0]);
        let diff = want - ang; while (diff > Math.PI / 2) diff -= Math.PI; while (diff < -Math.PI / 2) diff += Math.PI;
        const steps = Math.ceil(Math.abs(diff) / (10 * Math.PI / 180));
        for (let i = 1; i <= steps; i++) {
          const t = ang + diff * i / steps;
          a = [m[0] - h * Math.cos(t), m[1] - h * Math.sin(t)]; c = [m[0] + h * Math.cos(t), m[1] + h * Math.sin(t)]; put();
        }
        f.push("Q rotated");
      }
      for (let i = 0; i < 10; i++) { a = [a[0] + drag[0] / 10, a[1] + drag[1] / 10]; c = [c[0] + drag[0] / 10, c[1] + drag[1] / 10]; put(); }
      f.push("Q dragged", "H 6", "U", "Q lifted");
      return f;
    };
    const drags = [[-200, -80], [200, -80], [-200, 80], [200, 80], [-80, -200], [80, -200], [-80, 200], [80, 200], [-200, 0], [0, 200]];
    for (const [name, rotate] of (process.env.ROTATE_ONLY ? [["rotate across", true], ["rotate again", true]] : [["sideways", false], ["rotate across", true]])) {
      for (const drag of drags) {
        await reset();
        const reads = await phases(seq([1, 0], drag, rotate));
        const g = l => reads.find(r => r.label === l);
        if (!g("zoomed") || !g("dragged")) { console.log(`${name} drag ${drag}: no reads (window covered?)`); continue; }
        const k = g("zoomed").s.scale * g("zoomed").s.dpr;
        const mv = (p, q) => { const u = under(g(p).s), v = under(g(q).s); return [(u[0] - v[0]) * k, (u[1] - v[1]) * k]; };
        const st0 = mv("down", "started"), zm = mv("started", "zoomed"), rt = rotate ? mv("zoomed", "rotated") : [0, 0];
        const dr = mv(rotate ? "rotated" : "zoomed", "dragged");
        const pct = (gv, w) => w ? (100 * gv / w).toFixed(0) + "%" : gv.toFixed(1) + "px";
        console.log(`${name.padEnd(14)} drag ${String(drag).padEnd(9)} start ${st0.map(v => v.toFixed(1))} zoom ${zm.map(v => v.toFixed(1))}` +
          (rotate ? ` turn ${rt.map(v => v.toFixed(1))}` : "") + `  drag ${pct(dr[0], drag[0])},${pct(dr[1], drag[1])}  x${g("zoomed").s.scale.toFixed(3)}`);
      }
    }
  } else if (which === "speed") {
    // fingers across the drag (sideways fingers, vertical drag): overshoot vs speed, and endings that slow down
    const seqv = (steps, ms, ending) => {
      let a = [cx - 120, cy], c = [cx + 120, cy];
      const f = [`P ${a[0]} ${a[1]} ${c[0]} ${c[1]}`, "S 30"];
      a = [a[0] - 15, a[1]]; c = [c[0] + 24.5, c[1]]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`, "S 24");
      a = [a[0] + 1, a[1]]; c = [c[0] - 1, c[1]]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`, "S 24");
      const mx = (a[0] + c[0]) / 2;
      for (let i = 0; i < 10; i++) { const k = Math.pow(1.2, 0.1); a = [mx + k * (a[0] - mx), a[1]]; c = [mx + k * (c[0] - mx), c[1]]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`); }
      f.push("Q zoomed");
      const total = -300;
      let dys = Array(steps).fill(1);
      if (ending === "ease") dys = [...Array(steps - 4).fill(1), 0.6, 0.35, 0.2, 0.1];
      const sum = dys.reduce((x, y) => x + y, 0);
      let first = true;
      for (const d of dys) {
        const extra = first && ending === "extra9" ? -9 : 0; first = false;
        a = [a[0], a[1] + d * total / sum + extra]; c = [c[0], c[1] + d * total / sum + extra]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`, `S ${ms}`);
      }
      if (ending === "wiggle") for (let i = 0; i < 4; i++) f.push(`M ${a[0]} ${a[1] + 1} ${c[0]} ${c[1] + 1}`, "S 16", `M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`, "S 16");
      f.push("Q dragged", "H 6", "U", "Q lifted");
      return f;
    };
    const runs = process.env.CASES === "steps"
      ? [["6 steps of 50", 6, 8, "hold"], ["4 steps of 75", 4, 8, "hold"], ["3 steps of 100", 3, 8, "hold"], ["2 steps of 150", 2, 8, "hold"], ["10 steps of 30", 10, 8, "hold"]]
      : process.env.CASES === "jumps"
      ? [["one 300 jump", 1, 8, "hold"], ["10 steps, +9 first", 10, 8, "extra9"], ["10 steps", 10, 8, "hold"], ["one 300 jump, +9", 1, 8, "extra9"]]
      : [["fast 10x2ms", 10, 2, "hold"], ["medium 10x8ms", 10, 8, "hold"], ["slow 10x30ms", 10, 30, "hold"],
         ["fast, ease out", 10, 2, "ease"], ["fast, wiggle", 10, 2, "wiggle"], ["slow 30x10ms", 30, 10, "hold"]];
    for (const [name, steps, ms, ending] of runs) {
      await reset();
      const reads = await phases(seqv(steps, ms, ending));
      const z = reads.find(r => r.label === "zoomed"), d = reads.find(r => r.label === "dragged"), l = reads.find(r => r.label === "lifted");
      if (!z || !d) { console.log(name, "no reads"); continue; }
      const k = z.s.scale * z.s.dpr, uz = under(z.s), ud = under(d.s), ul = under(l.s);
      console.log(`${name.padEnd(18)} drag -300: page moved ${((uz[1] - ud[1]) * k).toFixed(1)}  after lift ${((ud[1] - ul[1]) * k).toFixed(1)}  x${z.s.scale.toFixed(3)}`);
    }
  } else if (which === "rapid") {
    for (const [name, axis] of [["sideways", [1, 0]], ["upright", [0, 1]]])
      for (const drag of [[-150, 0], [150, 0], [0, -150], [0, 150], [-270, -108]])
        await report(`kick(+,+) ${name} rapid`, { kick: [1, 1], axis, drag, ending: "rapid" });
  } else if (which === "rails") {
    // fingers at right angles to each drag (that alone is exact): what does the kick do to axis locking?
    const drags = [[-200, -80], [200, -80], [-200, 80], [200, 80], [-80, -200], [80, -200], [-80, 200], [80, 200]];
    for (const [name, opts] of [["no kick", {}], ["kick(+,+)", { kick: [1, 1] }], ["kick(-,+)", { kick: [-1, 1] }], ["kick along drag", { kickAlong: 1 }]])
      for (const drag of drags) {
        const n = Math.hypot(drag[0], drag[1]);
        const axis = [-drag[1] / n, drag[0] / n];
        const kick = opts.kickAlong ? [drag[0] / n * Math.SQRT2, drag[1] / n * Math.SQRT2] : opts.kick;
        await report(name, { kick, axis, drag });
      }
  } else if (which === "order") {
    for (const drag of [[-150, 0], [150, 0]]) {
      for (const [name, opts] of [["10 steps", {}], ["10 steps reversed", { order: "reversed" }], ["10 steps alternating", { order: "alternate" }],
                                  ["50 steps", { steps: 50 }], ["150 steps", { steps: 150 }]])
        await report(`sideways ${name}`, { kick: [1, 1], drag, ...opts });
    }
  } else if (which === "axes") {
    for (const [name, axis] of [["sideways", [1, 0]], ["upright", [0, 1]], ["diagonal", [Math.SQRT1_2, Math.SQRT1_2]]])
      for (const drag of [[-150, 0], [150, 0], [0, -150], [0, 150]])
        for (let rep = 0; rep < 2; rep++) await report(`kick(+,+) ${name} #${rep}`, { kick: [1, 1], axis, drag });
  } else if (which === "viewports") {
    for (const drag of [[-270, 0], [270, 0], [0, -200], [0, 200], [-100, 0], [100, 0]])
      await report("kick(+,+) hold", { kick: [1, 1], drag });
  } else if (which === "endings") {
    for (const ending of ["hold", "ease", "wiggle", "wiggle-late", "repeat"])
      for (const drag of [[-270, -108], [270, 0], [0, 200]])
        await report(`kick(+,+) ${ending}`, { kick: [1, 1], drag, ending });
    await report("kick(+,+) hold, 1 big step", { kick: [1, 1], drag: [-270, -108], steps: 1 });
    await report("kick(+,+) ease, 1 big step", { kick: [1, 1], drag: [-270, -108], ending: "ease" });
  } else {
    const drags = [[-200, -80], [200, -80], [-200, 80], [200, 80], [-80, -200], [80, 200]];
    for (const [name, opts] of [["no kick, sideways", {}], ["no kick, upright", { axis: [0, 1] }], ["no kick, diagonal", { axis: [Math.SQRT1_2, Math.SQRT1_2] }],
                                ["kick(+,+)", { kick: [1, 1] }], ["kick(-,+)", { kick: [-1, 1] }]])
      for (const drag of drags) await report(name, { ...opts, drag, ending: "ease" });
  }
} finally { b.close(); run("drive.exe", ["setcursor", saved[0], saved[1]]); }
