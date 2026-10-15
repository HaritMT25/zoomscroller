"""Applies one deliberate bug at a time to src/Engine.cs (in a temp copy) and checks the engine tests catch it.
Run from the repo root: python tests/mutation_check.py [name filter]"""
import os, subprocess, sys

CSC = os.path.join(os.environ["WINDIR"], r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, "work")

# Not listed, as no test can see them: the start's midpoint correction (under half a pixel, random), the x1.5
# minimum zoom room (efficiency: with less room the engine re-grips more often but still gets there), and turns
# keeping Chrome's span rather than repaying leftovers (every zoom repays them, so they stay tiny), the turns'
# midpoint correction (half a pixel at most per step), a zoom repaying leftovers mid-gesture (a backstop: grip
# starts already settle on the rounding that cancels them), and carrying a sub-pixel drag remainder into the next
# gesture (with a start that doesn't scroll, such remainders are rare and tiny), and turns snapping to the current
# span rather than the settled one (small turns mostly land on the very same rounded span).
MUTANTS = [
    ("pinch start moves finger 0 too far",
     "int wax0 = Native.Px(-p.Ux * In0), way0 = Native.Px(-p.Uy * In0), wbx0 = Native.Px(p.Ux * Out1), wby0 = Native.Px(p.Uy * Out1);",
     "int wax0 = Native.Px(-p.Ux * In0 * 1.4), way0 = Native.Px(-p.Uy * In0 * 1.4), wbx0 = Native.Px(p.Ux * (Out1 - In0 * 0.4)), wby0 = Native.Px(p.Uy * (Out1 - In0 * 0.4));"),
    ("pinch start moves only finger 1",
     "int wax0 = Native.Px(-p.Ux * In0), way0 = Native.Px(-p.Uy * In0), wbx0 = Native.Px(p.Ux * Out1), wby0 = Native.Px(p.Uy * Out1);",
     "int wax0 = 0, way0 = 0, wbx0 = Native.Px(p.Ux * (In0 + Out1)), wby0 = Native.Px(p.Uy * (In0 + Out1));"),
    ("fingers go down on the cursor, not short of it", "int sx = Native.Px(2 * p.Mx) - wax0 - wbx0, sy = Native.Px(2 * p.My) - way0 - wby0;",
     "int sx = Native.Px(2 * p.Mx), sy = Native.Px(2 * p.My);"),
    ("turns while a drag reverses", "if (dirX * dirX + dirY * dirY < 0.6 * 0.6) return false;", "if (dirX == 0 && dirY == 0) return false;"),
    ("no step cap", "double c = want > cap ? cap / want : 1;", "double c = 1;"),
    ("zoom after a drag without re-grip", "if (fx * fx + fy * fy > 9 * dpi * dpi)", "if (false)"),
    ("no narrowing back", "double[] snap = Snap(mx - ux * h, my - uy * h, mx + ux * h, my + uy * h, reference);",
     "double[] snap = new[] { ax, ay, bx, by };"),
    ("start steps not apart", "if (now - stageAt < StartGapMs)", "if (false)"),
    ("narrowing's half pixel not owed back", "pendX -= (ax + bx) / 2 - mx; pendY -= (ay + by) / 2 - my;\n                            holdSpan", "holdSpan"),
    ("start snap ignores leftovers", "zoomDebt + Math.Log(span / ideal)", "Math.Log(span / ideal)"),
    ("no turning across drags", "moved = Turn();", "moved = false;"),
    ("turn steered by corrections", "if (dirX * dirX + dirY * dirY < 0.6 * 0.6) return false;  // no steady direction: don't turn",
     "double nn = Math.Sqrt(pendX * pendX + pendY * pendY); dirX = pendX / nn; dirY = pendY / nn;"),
    ("pan re-grips along the drag", "options.Add(new Placement { Score = 2,", "options.Add(new Placement { Score = 0.5,"),
    ("straddles not preferred", "options.Add(Score(new Placement { Mx = cx, My = cy, Ux = qx, Uy = qy, Down = pS }, dir, 10));",
     "options.Add(Score(new Placement { Mx = cx, My = cy, Ux = qx, Uy = qy, Down = pS }, dir, 0.1));"),
    ("rounding not counted at a lift", "if (pinchStarted) zoomDebt += Math.Log(ChromeSpan() / Dist(ax, ay, bx, by));", ""),
    ("zoom about the midpoint", "double k = Math.Exp(dl);", "double k = Math.Exp(dl); double cx = (ax + bx) / 2, cy = (ay + by) / 2;"),
    ("pan fraction ignored", "return Math.Max(0, t);", "return 1;"),
    ("no hold before lift", "liftAt = now + (final && Momentum ? 0 : HoldMs);", "liftAt = now;"),
    ("momentum ignored", "liftAt = now + (final && Momentum ? 0 : HoldMs);", "liftAt = now + HoldMs;"),
    ("min span ignored", "Dist(nax, nay, nbx, nby) >= MinSpan &&", ""),
    ("release drops pending zoom", "if (releaseWanted && pendingLog == 0", "if (releaseWanted"),
    ("hit check ignored", "if (!(HitOk(a0x, a0y) && HitOk(b0x, b0y))) continue;", ""),
    ("pan lost on re-grip", "pendX -= mx; pendY -= my;", "pendX = 0; pendY = 0;"),
    ("inverted pan ignored", "double k = InvertPan ? -PanSpeed : PanSpeed;", "double k = PanSpeed;"),
    ("touch-down below Chrome's minimum", "double pS = dir > 0 ? MinSpan : wide;", "double pS = dir > 0 ? MinSpan * 0.85 : wide;"),
    ("no easing cap", "if (Math.Abs(dl) > 0.003) dl *= 1 - Math.Exp(-dt / SmoothMs);", "dl *= 1 - Math.Exp(-dt / SmoothMs);"),
    ("cancelled contacts not re-pressed", "if (!Grip(pendingLog >= 0 ? 1 : -1, now)) state = State.Idle;", "state = State.Idle;"),
]
NATIVE_MUTANTS = []

def run(name, path, old, new):
    src = open(os.path.join(ROOT, path)).read()
    assert old in src, "pattern missing for " + name
    tmp = os.path.join(WORK, "mut_" + os.path.basename(path))
    open(tmp, "w").write(src.replace(old, new, 1))
    src_dir, tests_dir = os.path.join(ROOT, "src"), os.path.join(ROOT, "tests")
    files = {"src/Native.cs": os.path.join(src_dir, "Native.cs"), "src/Engine.cs": os.path.join(src_dir, "Engine.cs")}
    files[path] = tmp
    exe = os.path.join(WORK, "mut.exe")
    cmd = [CSC, "-nologo", "-platform:x64", "-out:" + exe, files["src/Native.cs"], os.path.join(src_dir, "Fingers.cs"),
           files["src/Engine.cs"], os.path.join(src_dir, "Settings.cs"), os.path.join(tests_dir, "EngineTests.cs")]
    if subprocess.run(cmd, cwd=ROOT, capture_output=True).returncode:
        return name, "BUILD FAILED", ""
    out = subprocess.run([exe], cwd=ROOT, capture_output=True, text=True).stdout.strip().splitlines()
    first = next((l for l in out if l.startswith("FAIL")), "")
    return name, out[-1] if out else "?", first

os.makedirs(WORK, exist_ok=True)
flt = sys.argv[1] if len(sys.argv) > 1 else ""
survivors = 0
for name, old, new in MUTANTS:
    if flt in name:
        n, res, first = run(name, "src/Engine.cs", old, new)
        caught = not res.endswith(" 0 failures")
        survivors += not caught
        print(("caught   " if caught else "SURVIVED ") + n + "  (" + res + ")  " + first[:90])
for name, old, new in NATIVE_MUTANTS:
    if flt in name:
        n, res, first = run(name, "src/Native.cs", old, new)
        caught = not res.endswith(" 0 failures")
        survivors += not caught
        print(("caught   " if caught else "SURVIVED ") + n + "  (" + res + ")  " + first[:90])
print(f"{survivors} survived")
sys.exit(1 if survivors else 0)
