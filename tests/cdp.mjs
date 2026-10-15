// Shared helpers: launch a throwaway, headful Chrome (own profile) on tests/page.html and talk CDP to it.
import { spawn, execFileSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
export const ROOT = join(here, "..");
const CHROME = process.env.CHROME || "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const sleep = ms => new Promise(r => setTimeout(r, ms));
export { sleep };

export async function launch(port = 9461, extraArgs = [], pageUrl = null) {
  const profile = mkdtempSync(join(tmpdir(), "pinch-chrome-"));
  const url = pageUrl || pathToFileURL(join(here, "page.html")).href;
  const proc = spawn(CHROME, [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--no-first-run",
    "--no-default-browser-check", "--disable-extensions", "--window-position=60,60", "--window-size=1000,700",
    ...extraArgs, url], { stdio: "ignore" });
  let page;
  for (let i = 0; i < 100 && !page; i++) {
    await sleep(200);
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      page = list.find(p => p.type === "page" && (pageUrl ? p.url.startsWith(pageUrl.slice(0, 20)) : p.url.startsWith("file:")));
    } catch { }
  }
  if (!page) throw new Error("test chrome did not come up");
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let seq = 0; const pending = new Map();
  ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); } };
  const cmd = (method, params = {}) => new Promise(res => { const id = ++seq; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
  const evaluate = async expr => {
    const r = await cmd("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true });
    if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails));
    return r.result?.result?.value;
  };
  await cmd("Page.enable");
  for (let i = 0; i < 50 && !pageUrl; i++) { if (await evaluate("typeof window.state === 'function'")) break; await sleep(100); }
  const close = () => { try { ws.close(); } catch { } try { proc.kill(); } catch { }
    try { execFileSync("taskkill", ["/F", "/T", "/PID", String(proc.pid)], { stdio: "ignore" }); } catch { }
    setTimeout(() => { try { rmSync(profile, { recursive: true, force: true }); } catch { } }, 1500); };
  return { cmd, evaluate, close, proc, pid: proc.pid };
}

// Centre of the page viewport in physical screen pixels.
export async function viewportCentre(evaluate) {
  return evaluate(`(() => { const d = devicePixelRatio, b = (outerWidth - innerWidth) / 2;
    return { x: Math.round((screenX + outerWidth / 2) * d),
             y: Math.round((screenY + outerHeight - innerHeight - b + innerHeight / 2) * d),
             left: Math.round((screenX + b) * d), top: Math.round((screenY + outerHeight - innerHeight - b) * d),
             w: Math.round(innerWidth * d), h: Math.round(innerHeight * d) }; })()`);
}

export function run(exe, args) {
  try { return { code: 0, out: execFileSync(join(ROOT, "bin", exe), args.map(String), { encoding: "utf8" }) }; }
  catch (e) { return { code: e.status, out: (e.stdout || "") + (e.stderr || "") }; }
}
