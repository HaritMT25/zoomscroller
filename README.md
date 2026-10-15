# Mouse Pinch

Touchpad-style pinch zoom for Chrome and Edge with a mouse. It zooms **the page**, not the browser's
zoom setting, it stops at 100%, it zooms where the cursor is, and you can move around while zoomed.
This is the zoom you get from two fingers on a touchpad.

| Do this                                   | You get                                              |
|-------------------------------------------|------------------------------------------------------|
| Hold **Ctrl**, wheel up                   | zoom in where the cursor is                          |
| Keep Ctrl held, move the mouse            | the page slides under the cursor (move around)       |
| Ctrl, wheel down                          | zoom back out, stopping at 100%                      |
| Let go of Ctrl                            | the page stays zoomed                                |
| Plain wheel while zoomed                  | scrolls inside the zoomed view                       |

Ctrl + plus/minus still change the browser's own zoom. The tray icon (a magnifier) has the settings:
on/off (double-click), zoom speed, drag direction, glide after letting go, start with Windows.

## The helper extension (recommended)

Some sites forbid touch pinch-zoom with the CSS `touch-action` property. YouTube does it on its home
grid, and the same rule also blocks touch scrolling one way. With the helper, Mouse Pinch can't be
blocked; without it, zoom and drag do nothing on such parts of a page. The helper only resets
`touch-action`. That property only ever affects touch input, so your mouse, touchpad and keyboard
behave exactly as before.

To install it in Chrome (or Edge):

1. Open `chrome://extensions` (in Edge, `edge://extensions`).
2. Switch on **Developer mode**.
3. Click **Load unpacked** and pick the `extension` folder in this repository.

## How it works

Windows lets programs inject touch input. Mouse Pinch watches the mouse and keyboard with low-level
hooks. On Ctrl + wheel over a Chromium page, it puts two virtual fingers on the page, with their
midpoint on the cursor. Each wheel notch spreads the fingers apart or brings them together: a plain
pinch about the cursor, which Chrome itself keeps still. Mouse motion moves both fingers together, and
the page slides under the cursor. When a finger would leave the page, the fingers are lifted and put
down again, like lifting your fingers off a touchpad.

Why fake touchscreen fingers and not a fake touchpad: Windows doesn't let a program inject touchpad input
(`CreateSyntheticPointerDevice` refuses `PT_TOUCHPAD`; see `tests/TouchpadProbe.cs`). A virtual touchpad
would need a signed kernel driver.

`src/Engine.cs` holds the gesture logic. Chrome's touchscreen handling has quirks, all measured on
Chrome 155 (`tests/phase_test.mjs`, `tests/engine_phase_test.mjs`, `tests/site_test.mjs`) and
cross-checked against Chromium's `ui/events/gesture_detection` and `cc` source:

- A pinch needs the fingers 125 DIP apart. It starts after a 30 DIP span change; that much is swallowed
  and any excess zooms. Pinch zoom tops out at 4×.
- A scroll starts once the fingers' midpoint has moved 7.2 DIP, and that much is swallowed. When a
  pinch starts, scrolling starts with it and nothing is swallowed.
- If the first finger moves 15 DIP before a pinch is on, Chrome locks the scroll to an axis ("rails")
  until the drag strays about 70 px off it.
- Each finger's update reaches Chrome on its own. A drag along the line between the fingers therefore
  briefly changes their span, which costs up to 6% of the drag. A drag across that line costs nothing.
  A single big jump scrolls short: 300 px came out as 223.
- Lifting fingers that moved within the last ~100 ms flings the page.
- A pinch that starts within 100 DIP of the page's edge zooms about that edge. Chrome pins the edge.
- Updates that arrive in a burst get merged. Near 100%, a zoom-out right after a scroll shifts the page.

So every grip starts like this:

1. The fingers go down on whole pixels.
2. The fingers widen past the pinch threshold, each step on its own and about 16 ms apart. The first
   finger moves less than the axis-lock threshold and the midpoint less than the scroll threshold, so the
   pinch starts with no scroll and no axis lock, and its midpoint lands exactly on the cursor.
3. The fingers narrow back about that midpoint, which undoes the excess zoom where it happened.

After that:

- Zooming is a pinch about the cursor. A zoom that starts after a drag grips again on the cursor first.
- Dragging turns the fingers to lie across the drag and moves them at most 60 px per update.
- Before any lift the fingers stand still for 110 ms, so nothing flings.
- Leftovers from whole-pixel rounding are tracked and cancelled, so neither the zoom nor the position creeps.

**Known limits:**

- Chrome ignores mouse clicks for about half a second after any touch input. A click right after you
  let go of Ctrl can be lost.
- Near the page's edge (within about 100 DIP), Chrome pins the zoom to the edge instead of the cursor.
- A drag that scrolls the page itself, beyond the zoomed view, can run a few percent further than the mouse.

## Build and test

```
powershell -ExecutionPolicy Bypass -File build.ps1 -Tests     # bin\MousePinch.exe + test tools (csc from .NET Framework 4)
bin\engine_tests.exe                                         # engine against a model of Chrome's pinch handling
python tests\mutation_check.py                               # every listed deliberate bug must make a test fail
node tests\e2e_test.mjs                                      # real Chrome (throwaway profile), tagged SendInput
node tests\engine_phase_test.mjs                             # the real engine on real Chrome, phase by phase
node tests\site_test.mjs [--css] [--drag] [url]              # the real engine on a real site (default: YouTube)
```

The live tests open their own Chrome window and move your cursor for about a minute, so leave the mouse
alone while they run. They only inject touch into their own test window (checked before every gesture),
and they put the cursor back at the end. A `--test` instance of the app only obeys the tests' tagged input, never your real mouse.

Settings: `%APPDATA%\MousePinch\settings.ini`. Log: `%APPDATA%\MousePinch\mousepinch.log`.
