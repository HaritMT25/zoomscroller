"""Applies one deliberate bug at a time to src/Engine.cs (in a temp copy) and checks the engine tests catch it.
Run from the repo root: python tests/mutation_check.py [name filter]"""
import os, subprocess, sys

CSC = os.path.join(os.environ["WINDIR"], r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, "work")

# Not listed, as no test can see them: the start's midpoint correction (under half a pixel, random), the x1.5
# minimum zoom room (efficiency: with less room the engine re-grips more often but still gets there), and turns
# keeping Chrome's span rather than repaying leftovers (every zoom repays them, so they stay tiny), the turns'
# midpoint correction (half a pixel at most per step) and a zoom repaying leftovers mid-gesture (a backstop: grip
# starts already settle on the rounding that cancels them).
MUTANTS = [
    ("pinch start moves both fingers", "bx += ux * Out; by += uy * Out;",
     "bx += ux * Out / 2; by += uy * Out / 2; ax -= ux * Out / 2; ay -= uy * Out / 2;"),
    ("no step back after the start", "Translate(tx, ty);", ""),
    ("start step back may leave the page", "ty = Clamp(ty, Math.Ceiling(Math.Max(T - ay, T - by)), Math.Floor(Math.Min(B - ay, B - by)));", ""),
    ("start remainder dropped", "if (Math.Abs(pendX) >= 1 || Math.Abs(pendY) >= 1) pendX = pendY = 0;", "pendX = pendY = 0;"),
    ("step back before narrowing", "else if (startStage == 1)", "else if (startStage == 9)"),
    ("start steps not apart", "if (now - stageAt < StartGapMs)", "if (false)"),
    ("start snap ignores leftovers", "zoomDebt + Math.Log(span / ideal)", "Math.Log(span / ideal)"),
    ("no turning across drags", "moved = Turn();", "moved = false;"),
    ("turn steered by corrections", "if (dirX == 0 && dirY == 0) return false;",
     "double nn = Math.Sqrt(pendX * pendX + pendY * pendY); dirX = pendX / nn; dirY = pendY / nn;"),
    ("pan re-grips along the drag", "options.Add(new Placement { Score = 2,", "options.Add(new Placement { Score = 0.5,"),
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
    ("touch-down below Chrome's minimum", "double pS = dir > 0 ? MinSpan : pMax;", "double pS = dir > 0 ? MinSpan * 0.85 : pMax;"),
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
