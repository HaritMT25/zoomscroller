// Experiments on real Chrome (results printed, nothing asserted):
//  1. drags after the engine's touch-down sequences: does the page follow both axes (rails? swallowed slop?)
//  2. how soon after a touch gesture does Chrome accept a mouse click again
import { launch, run, sleep } from "./cdp.mjs";

const b = await launch(9463);
const saved = run("drive.exe", ["cursor"]).out.trim().split(/\s+/).map(Number);
try {
  await b.cmd("Page.bringToFront"); await sleep(800);
  const guess = await b.evaluate("({ x: Math.round((screenX + outerWidth / 2) * devicePixelRatio), y: Math.round((screenY + outerHeight - innerHeight / 2) * devicePixelRatio) })");
  const where = run("probe.exe", ["where", "PINCHTEST", guess.x, guess.y]).out;
  const [L, T, R, B] = where.match(/rect (-?\d+) (-?\d+) (-?\d+) (-?\d+)/).slice(1).map(Number);
  const cx = Math.round((L + R) / 2), cy = Math.round((T + B) / 2);
  const st = () => b.evaluate("state()");
  const frames = f => { const r = run("probe.exe", ["frames", "PINCHTEST", cx, cy, f.join(";")]); if (!/ok=True/.test(r.out)) console.log(r.out); };
  const sym = s => `${cx - s / 2} ${cy} ${cx + s / 2} ${cy}`;
  // back to x1, scroll to a known spot, then a symmetric pinch at the centre to ~x2
  const setup = async () => {
    frames(["P " + sym(500), ...[400, 300, 200, 160].map(s => "M " + sym(s)), "H 6", "U"]);
    await sleep(200);
    await b.evaluate("scrollTo(400, 1200); 1"); await sleep(150);
    frames(["P " + sym(200), ...[240, 280, 320, 360, 400, 440, 475].map(s => "M " + sym(s)), "H 6", "U"]);
    await sleep(250);
  };

  // the gesture under test: touch-down at the centre (span 260, sideways), optional kick, optional pinch start, drag
  const gesture = (dx, dy, { kick, widen, kickFirst }) => {
    let a = [cx - 130, cy], c = [cx + 130, cy];
    const f = [`P ${a} ${c}`.replace(/,/g, " ")];
    const mv = (ax, ay, cx2, cy2) => { a = [a[0] + ax, a[1] + ay]; c = [c[0] + cx2, c[1] + cy2]; f.push(`M ${a[0]} ${a[1]} ${c[0]} ${c[1]}`); };
    const doKick = () => { mv(15, 15, 15, 15); mv(-2, -2, -2, -2); };
    const doWiden = () => { mv(-10, 0, 10, 0); mv(-10, 0, 10, 0); mv(1, 0, -1, 0); };
    if (kick && kickFirst) doKick();
    if (widen) doWiden();
    if (kick && !kickFirst) doKick();
    for (let i = 0; i < 20; i++) mv(dx / 20, dy / 20, dx / 20, dy / 20);
    f.push("H 8", "U");
    return f;
  };
  const angles = [[-150, 0], [-140, -50], [-106, -106], [-50, -140], [0, -150]];
  const variants = [
    ["plain", {}], ["kick", { kick: 1 }], ["widen", { widen: 1 }],
    ["widen, kick", { widen: 1, kick: 1 }], ["kick, widen", { widen: 1, kick: 1, kickFirst: 1 }],
  ];
  for (const [name, opts] of variants) {
    const row = [];
    for (const [dx, dy] of angles) {
      await setup();
      const s0 = await st();
      frames(gesture(dx, dy, opts));
      await sleep(300);
      const s1 = await st();
      const k = s0.scale * s0.dpr;
      const mx = -(s1.pl - s0.pl) * k, my = -(s1.pt - s0.pt) * k;
      row.push(`(${dx},${dy})->(${mx.toFixed(0)},${my.toFixed(0)})${Math.abs(s1.scale / s0.scale - 1) > 0.002 ? " z" + (s1.scale / s0.scale).toFixed(3) : ""}`);
    }
    console.log(name.padEnd(12), row.join("  "));
  }

  // clicks after a touch gesture
  for (const gap of [100, 300, 500, 700, 1000]) {
    const s0 = await st();
    frames(["P " + sym(300), "M " + sym(340), "M " + sym(300), "H 4", "U"]);
    run("drive.exe", [`at ${cx} ${cy + 100}; sleep ${gap}; click; sleep 200`]);
    const s1 = await st();
    console.log(`click ${gap} ms after a touch lift: ${s1.clicks - s0.clicks ? "reached the page" : "LOST"}`);
  }
  for (const gap of [100, 300]) {
    const s0 = await st();
    frames(["P " + sym(300), "M " + sym(340), "M " + sym(300), "H 4", "U"]);
    run("drive.exe", [`at ${cx} ${cy + 100}; move 3 0; move -3 0; sleep ${gap}; click; sleep 200`]);
    const s1 = await st();
    console.log(`click ${gap} ms after a lift, mouse moved first: ${s1.clicks - s0.clicks ? "reached the page" : "LOST"}`);
  }
} finally { b.close(); run("drive.exe", ["setcursor", saved[0], saved[1]]); }
