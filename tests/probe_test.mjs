// Feasibility: does an injected two-finger touch pinch zoom the PAGE (visualViewport) and not the browser zoom?
import { launch, viewportCentre, run, sleep } from "./cdp.mjs";

const b = await launch();
try {
  await b.cmd("Page.bringToFront");
  await sleep(800);
  const c = await viewportCentre(b.evaluate);
  console.log("viewport", c);
  console.log("where:", run("probe.exe", ["where", "PINCHTEST", c.x, c.y]).out.trim());
  console.log("before", await b.evaluate("state()"));

  console.log(run("probe.exe", ["pinch", "PINCHTEST", c.x, c.y, 200, 400, 200]).out.trim());
  await sleep(400);
  console.log("after pinch out x2", await b.evaluate("state()"));

  console.log(run("probe.exe", ["pan", "PINCHTEST", c.x, c.y, 200, -150, -100, 200]).out.trim());
  await sleep(600);
  console.log("after pan (-150,-100)", await b.evaluate("state()"));

  console.log(run("probe.exe", ["pinch", "PINCHTEST", c.x, c.y, 400, 50, 300]).out.trim());
  await sleep(400);
  console.log("after pinch in /8", await b.evaluate("state()"));
} finally { b.close(); }
