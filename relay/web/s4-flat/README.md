---
name: "s4-flat"
description: "Build any app, page, tool or mockup in S4 Flat, the user's flat Plate & Ink design language: circles and rounded shapes, monochrome default, shape-changing plates and ink-fill controls."
---

# S4 Flat v2.1 — Plate & Ink

S4 Flat is the user's own flat design language for desktop web apps and mobile pages. Use it whenever the user asks for S4 Flat, "my flat style", "Plate & Ink", or names this skill. Do not mix in S4 Glass or S4 Material styling (no glass, blur or shadows) unless asked. The user's words in the current request win over this skill.

Live reference built only from this kit: https://claude.ai/artifact/FuV5GiUC7r9mWRaAXeFV31

## What is in this folder

| Path | What |
|---|---|
| `assets/s4-flat.css` | The stylesheet: tokens, both themes, every component. Never retype or edit it in a page. |
| `assets/s4-flat.js` | The runtime (`window.S4Flat`): ink, glide, plates, morphs, menus, table sort, calendar, toasts. |
| `assets/remix-sprite.svg` | 106 Remix Icon symbols (line, plus fill for nav-style icons). |
| `assets/starter.src.html` | A starting page with the three build placeholders. |
| `assets/reference.src.html` | The full reference page source: a working example of every component. |
| `scripts/build_page.py` | Inlines the kit and only the icons the page uses, then runs the checker. |
| `scripts/screenshot.js` | Optional visual check: phone dark/light and desktop screenshots plus a sideways-overflow report. |
| `scripts/check_page.py` | Flags shadows, gradients, blur, sharp corners, literal colours, animated layout, heavy resets, missing icons. |
| `references/components.md` | Markup recipes, the runtime API, events and data attributes. Read it before writing markup. |

Find this folder with `dirname "$(find / -path '*s4-flat/SKILL.md' 2>/dev/null | head -1)"` if its path isn't obvious.

## How to build a page

1. Copy `assets/starter.src.html` to your working folder (for a large app, `assets/reference.src.html` shows every pattern).
2. Write the page in that source file. Keep the three placeholders: `/* S4FLAT:CSS */` at the top of `<style>`, `<!-- S4FLAT:ICONS -->` at the start of the body content, `<!-- S4FLAT:JS -->` before the app script. App CSS goes after the placeholder and uses tokens only. End the app script with `S4Flat.init()`.
3. Build: `python3 <skill>/scripts/build_page.py page.src.html page.html`. It inlines the kit, adds the icon symbols the page references (`<use href="#ri-NAME"/>`), and prints errors and warnings. Fix every error and every warning that is a real rule break, then rebuild.
4. Publish or deliver `page.html`. For claude.ai artifacts it is already self-contained. For a normal website you may instead ship `s4-flat.css` and `s4-flat.js` as files and link them, and still run `check_page.py` on the result.
5. Look at it once. Where the session allows, run `scripts/screenshot.js` (setup line at its top) and view the phone-dark and desktop shots: nothing wider than the screen, rows, chips and buttons showing their fills and padding, amounts visible in tables. Otherwise use the session's own preview.

Icons outside the bundled set: `npm pack remixicon@4.5.0 && tar xzf remixicon-4.5.0.tgz`, then build with `--remix package/icons`.

## The language

**Two materials.**
- **Plates** are the big things: panels, tiles, list rows, table rows, cards, sheets, dialogs, menus, the selection bar, toasts. Plates have weight and identity. They slide in from where they belong and change shape: a tile or row grows into its card, the + circle stretches into its sheet, a menu grows out of its button. They shrink back into where they came from. Only plates the user deliberately opens morph out of something; plates that appear as a consequence (the selection bar, toasts) rise quietly in their own place. While a plate sits on top, the page dims under a scrim and is inert (on screens 600px and wider it also eases back). Sheets drag down to close; rows swipe right to commit.
- **Ink** is the small controls you choose with: checks, chips, switches, radios, day choices, the confirm button, the drop zone. Ink stays put; colour pours from the exact touch point and drains back to it.

**Shape.** Only circles, pills and rounded rectangles; never a sharp corner.
- Actions (one-shot buttons) are pills or circles.
- Choices are soft squares that round into a circle or pill when chosen. Shape is part of the state.
- Anything that travels is round: the tab and nav beads and the switch knob are circles or pills. A switch knob is a circle that grows and draws a check when on, and stretches a little along its track while moving.
- Plates get rounder as they get bigger: 6–10 badges and dense cells, 16 fields, 22 rows and tiles, 30 panels, 36 sheets and dialogs. Row avatars are rounded squares; the user avatar is a circle.
- The mark (a soft square inside a circle) is the system's motif: logo, favicon (`S4Flat.favicon()`), empty states, and the loader (`.f-mark.loading`, the square turns and rounds).
- Focus and selection use a 2px inner ring that follows the shape, never a rectangle.

**Flat.** No shadows, gradients, blur or glass. Depth comes from tones (bg → surface → surface-2) and from plates moving. The scrim is the only translucent colour.

**Floating layout.** Nothing spans the width as a bar. The top bar is a floating pill. Navigation is a floating dock (dark nav pill + accent circle) at the bottom on phones and a vertical rail on the left from 1100px. From 1280px a card marked `data-side` docks beside the content as a side panel instead of covering it, and the page reserves its room automatically; below 1280px it opens as a centred card.

**Colour.** Monochrome by default. One accent per app via `S4Flat.setAccent('#hex')`: text on it is chosen black or white, and a line version (`--accent-line`) is derived so thin strokes, rings, dots and meters keep 3:1 on surface-2 even for light accents like yellow. Status colours (ok, warn, bad, info) are separate and always paired with a word or icon. `data-tint` on `<html>` washes the ground with the accent (5% light, 9% dark). Charts use `--chart-1` (accent line) then greys; one hue only.

**Type.** Inter, plain, tabular numbers on every figure. Headings 800/700 tight; labels Title Case; body sentence case. Money and percentages through `S4Flat.fmt`.

**Density.** `S4Flat.setDensity(true)` (or `data-density="compact"`) tightens rows, steps corners one size down and calms motion: plates fade in place instead of morphing and the page no longer recedes. Use compact for data-heavy desktop screens.

**Touch.** On touch screens chips, icon buttons and small buttons grow to 44px and whole settings rows toggle their switch.

## Motion rules

1. Ink fills, plates move. Small controls never travel; big things never flood with colour.
2. One motion per tap. Only what was touched moves; numbers, counts, totals, labels and status words it affects change in place with a quiet roll (`S4Flat.text`), never a snap and never a second big motion.
   Quiet is not instant: no visible text ever swaps in a single frame.
3. Settle, never bounce. One family of decelerating curves, no overshoot.
4. Leave faster than you arrive (exits 180ms).
5. Never block. Everything works mid-animation; a new tap redirects.
6. Only transform, opacity and clip animate. Layout size never animates (the tab bead's width is the one exception: it is absolutely positioned).
7. Quiet global changes: theme, accent, tint and density crossfade in 200ms.
8. Plates that share `data-f-vt="name"` morph between views (`S4Flat.view(fn)`) and between pages on real websites (`@view-transition` is on).
9. Long pages skip the recede scale; the scrim still dims. `data-f-recede="off"` on `<body>` turns it off.
10. Small screens move less. Below 600px the page doesn't recede, arrivals travel 10px instead of 16, cards unfold next to the row or tile that opened them, and any morph that would cross more than ~40% of the screen arrives in place instead. Motion should feel the same size on a phone as on a desktop, not bigger.
11. Morph only what was opened. A shape change says "this came from there"; use it only when the user tapped that thing to open it.
12. Touch: press states wait 70ms on touch screens so starting a scroll never flickers rows, and the kit enables `:active` on iOS.

| Move | Time | Used for |
|---|---|---|
| Press | 120ms | Everything you touch dips (.96; big plates .98) |
| Ink | 220ms | A small control turning on |
| Glide | 240ms | A bead sliding to the chosen tab or nav item; switch knob (with its stretch) |
| Text | 90 + 170ms | A value or label changing in place (`S4Flat.text`) |
| Plate | 300ms | Plates arriving, list reflow, sorting, shrinking back |
| Morph | 340ms | A plate changing shape; named view morphs |
| Recede | 320ms | The page easing back behind a plate |

## Writing

Plain, specific, active. Buttons name the action ("Approve", "Save Bill"). Toasts confirm in past tense and offer Undo for anything reversible. Destructive actions confirm in a dialog or offer Undo, never both. Errors say what went wrong and how to fix it. Use real content; mark examples as examples.

## Checklist before delivering

- `build_page.py` reports no errors; warnings are fixed or deliberate.
- Only circles, pills and rounded rectangles; no shadow, gradient, blur, sharp corner or rectangular focus outline.
- Big things move; small things fill; text that changes rolls via `S4Flat.text`; only opened plates morph out of their source.
- Every pressable has `f-tap`; every choice is an `f-ink` soft square that rounds when on.
- Page content inside `.f-wrap`; sheets, cards, dialogs, menus, the selection bar and the dock outside it.
- Monochrome unless the app has an accent; status uses `f-pill`/`f-banner` with words.
- Tabular numbers on all figures; money through `S4Flat.fmt.money`.
- Works at 400px with no sideways scroll (tables: `grow` on the main column, `f-hide-sm` on secondary ones), from 1100px (rail) and 1280px (side panel); both themes; reduced motion (the kit handles it).
