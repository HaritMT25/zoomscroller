# Store listing: ZoomScroller helper

Everything the Chrome Web Store dashboard asks for, ready to paste. The same text works for Microsoft Edge Add-ons.

## Submitting

1. Build the package: `powershell -ExecutionPolicy Bypass -File build.ps1 -Release` makes `dist\ZoomScroller-helper-<version>.zip`.
2. Chrome: open the [developer dashboard](https://chrome.google.com/webstore/devconsole), register as a developer
   (one-time $5 fee) and click **New item**. Upload the zip, then fill in the tabs below.
   Edge: [Partner Center](https://partner.microsoft.com/dashboard/microsoftedge/overview) is free; same zip, same text.
3. Submit for review. An extension that runs on all sites usually gets a longer review (days, sometimes weeks).
4. Once it's live, put its store link in the README's helper section.

## Store listing tab

**Name** (from the manifest): ZoomScroller helper

**Summary** (from the manifest): Lets the ZoomScroller app pinch-zoom every page: sites can no longer block touch zoom
with CSS. Mouse and touchpad unaffected.

**Category:** Accessibility

**Language:** English

**Description:**

```
Companion to ZoomScroller, a free Windows app that gives your mouse the touchpad's pinch zoom in Chrome and Edge:
hold Ctrl and turn the wheel to zoom into the page right where the cursor is, move the mouse to slide around,
and zoom back out to exactly 100%. It zooms the page itself, like a touchpad pinch, not the browser's zoom level.

ZoomScroller does this with touch input, and some sites (YouTube, for one) switch touch zoom off with the CSS
touch-action property. This helper turns that property back on for every page, so the zoom works everywhere.

- The ZoomScroller app is required: https://github.com/HaritMT25/zoomscroller
- It adds one CSS rule to pages and nothing else. No scripts, no data collected, nothing sent anywhere.
- touch-action only ever affects touchscreens, so your mouse, touchpad and keyboard behave exactly as before.
- Open source (MIT).
```

**Store icon:** `extension/icons/icon128.png`

**Screenshot:** `store/screenshot-1280x800.png`

**Small promo tile:** `store/promo-440x280.png`

**Official URL / homepage:** https://github.com/HaritMT25/zoomscroller

**Support URL:** https://github.com/HaritMT25/zoomscroller/issues

## Privacy tab

**Single purpose:**

```
Lets the ZoomScroller desktop app pinch-zoom web pages by resetting the CSS touch-action property, which some sites
use to block touch pinch-zoom.
```

**Host permission justification** (the content script matches all URLs):

```
The extension injects a single CSS rule (touch-action: auto) into every page, because any site can block touch
pinch-zoom with touch-action. It has no JavaScript, reads nothing from pages and sends nothing anywhere.
```

**Are you using remote code?** No.

**Data usage:** check none of the data types, then tick the three certifications (no selling, no unrelated use,
no creditworthiness use).

**Privacy policy URL:** https://github.com/HaritMT25/zoomscroller#privacy
