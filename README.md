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

## How it works

Windows lets programs inject touch input. Mouse Pinch watches the mouse and keyboard with low-level
hooks. On Ctrl + wheel over a Chromium page, it puts two virtual fingers on the page. Each wheel notch
moves the fingers apart or together, radially from the cursor, so Chrome keeps the point under the
cursor fixed. Mouse motion moves both fingers together. When a finger would leave the page, the fingers
are lifted and put down again, like lifting your fingers off a touchpad.

Why fake touchscreen fingers and not a fake touchpad: Windows doesn't let a program inject touchpad input
(`CreateSyntheticPointerDevice` refuses `PT_TOUCHPAD`; see `tests/TouchpadProbe.cs`). A virtual touchpad
would need a signed kernel driver.

`src/Engine.cs` holds the gesture logic. Chrome's touchscreen handling has quirks, all measured on
Chrome 155 (`tests/phase_test.mjs`, `tests/engine_phase_test.mjs`) and cross-checked against Chromium's
`ui/events/gesture_detection` source:

- A pinch needs the fingers 125 DIP apart. It starts after a 30 DIP span change; that much is swallowed
  and any excess zooms. Pinch zoom tops out at 4×.
- A scroll starts once the fingers' midpoint has moved 7.2 DIP, and that much is swallowed.
- If the first finger moves 15 DIP before a pinch is on, Chrome locks the scroll to an axis ("rails")
  until the drag strays about 70 px off it.
- Each finger's update reaches Chrome on its own. A drag along the line between the fingers therefore
  briefly changes their span, which costs up to 6% of the drag. A drag across that line costs nothing.
- Near 100%, the zoomed view has no room to move inside the page. A zoom-out right after a scroll then
  can't hold its anchor, and the page jumps.
- Updates that arrive in a burst get merged.

So every grip starts like this:

1. The fingers go down on whole pixels.
2. Only the second finger moves out past the pinch threshold, so the pinch starts before the first finger
   ever moves and no axis lock is decided.
3. The fingers narrow back about their midpoint, which undoes the excess zoom at the point where it happened.
4. Both fingers step back by exactly the scroll Chrome made.

The steps are about 24 ms apart. While you drag, the fingers turn to lie across the drag. Leftovers from
whole-pixel rounding are tracked and cancelled, so neither the zoom nor the position creeps.

**Known limit:** Chrome ignores mouse clicks for about half a second after any touch input. A click
right after you let go of Ctrl can be lost.

## Build and test

```
powershell -ExecutionPolicy Bypass -File build.ps1 -Tests     # bin\MousePinch.exe + test tools (csc from .NET Framework 4)
bin\engine_tests.exe                                         # engine against a model of Chrome's pinch handling
python tests\mutation_check.py                               # every listed deliberate bug must make a test fail
node tests\e2e_test.mjs                                      # real Chrome (throwaway profile), tagged SendInput
node tests\engine_phase_test.mjs                             # the real engine on real Chrome, phase by phase
```

The live tests open their own Chrome window and move your cursor for about a minute, so leave the mouse
alone while they run. They only inject touch into a window titled `PINCHTEST`, and they put the cursor
back at the end. A `--test` instance of the app only obeys the tests' tagged input, never your real mouse.

Settings: `%APPDATA%\MousePinch\settings.ini`. Log: `%APPDATA%\MousePinch\mousepinch.log`.
