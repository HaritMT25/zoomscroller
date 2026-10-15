// Measures Chrome's pinch thresholds: smallest finger span that still zooms, and whether diagonal pans are railed.
import { launch, viewportCentre, run, sleep } from "./cdp.mjs";

const b = await launch();
const P = (...a) => run("probe.exe", a).out.split("\n").filter(l => /inject|REFUSED/.test(l)).join(" ").trim();
const S = async label => { const s = await b.evaluate("state()"); console.log(label.padEnd(28), "scale", s.scale.toFixed(3), "vx", s.vx.toFixed(1), "vy", s.vy.toFixed(1), "sx", s.sx, "sy", s.sy, "dpr", s.dpr); return s; };
try {
  await b.cmd("Page.bringToFront"); await sleep(800);
  const c = await viewportCentre(b.evaluate);
  P("pinch", "PINCHTEST", c.x, c.y, 200, 1000, 300); await sleep(300);
  const s1 = await S("zoomed in");
  // shrink slowly from 600 to 60: zoom stops once the span is under Chrome's minimum
  P("pinch", "PINCHTEST", c.x, c.y, 600, 60, 900); await sleep(300);
  const s2 = await S("after 600->60");
  const ref = 600 - 37.5;  // span slop eaten at the start
  console.log("effective stop span ~", (ref * s2.scale / s1.scale).toFixed(0), "physical px (", (ref * s2.scale / s1.scale / s1.dpr).toFixed(0), "DIP )");

  P("pinch", "PINCHTEST", c.x, c.y, 200, 900, 300); await sleep(300);
  const a = await S("zoomed for pans");
  P("pan", "PINCHTEST", c.x, c.y, 300, 0, -150, 250); await sleep(500);
  const b1 = await S("pan up 150");
  P("pan", "PINCHTEST", c.x, c.y, 300, -150, -150, 250); await sleep(500);
  await S("pan diag 150,150");
} finally { b.close(); }
