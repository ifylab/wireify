# Wireify app kit

The `.ify` design language plus the live-sync machinery for this definition's companion
app, in plain HTML/CSS/JS — no build step, no network fetches, served locally by the
plugin. Read this before building or reshaping the page.

## Ownership — the one rule that matters

- `kit/` is Wireify's. It is re-stamped on every Build, and `scaffold_app` re-stamps
  it too — callable from a live session, so picking up a kit update never costs a
  working terminal. **Never edit anything inside `kit/`** — your changes would be
  overwritten.
- `index.html`, `app.js` (if you add one), `manifest.json`, and `theme.css` are yours
  (the agent's/user's). Wireify seeds them once and never touches them again.
- Re-theming happens ONLY in `theme.css`: it loads after the kit CSS, so any `--ify-*`
  token you redefine there wins.
- `kit/fixture.html` is the raw-contract reference page (plain fetch + EventSource, no
  kit imports) — the smallest working example of the re-shape rung, and the page the
  plugin's own test harness gates on.

## Wiring a page

Link order (the starter already does this):

```html
<link rel="icon" href="./kit/ify-mark.svg" type="image/svg+xml">
<link rel="stylesheet" href="./kit/ify-fonts.css">
<link rel="stylesheet" href="./kit/ify-tokens.css">
<link rel="stylesheet" href="./kit/ify-app.css">
<link rel="stylesheet" href="./theme.css">
```

Plus the pre-paint theme script in `<head>` (copy it from the starter — it cannot live
in a module) and `data-theme="auto"` on `<html>` (follow the OS; `?theme=` overrides).

## The binding core (`kit/ify-app.js`)

The core is headless — it builds no DOM and assumes nothing about presentation. Any
custom frontend, however unusual, should ride it rather than reimplement sync:

```js
import { connect, mount, bind, fmt, theme, chart } from './kit/ify-app.js';

const app = connect();               // SSE + reconnect + shared stream across tabs
app.onState((state) => { ... });     // every frame: {isActiveCanvas, docName, docUnits, tolerance, controls, views, warnings}
app.onStatus(({kind, text, id}) => {}); // live | stale | blocked | background | closed | disconnected | error; id = the control a refused push came from. connect()'s own live status always reads "live"; a custom app object (a standalone port's local data layer) supplies its own live text and mount shows it
app.control(id);                     // latest state for one declared control
app.push(id, value);                 // debounced (drags); {immediate:true} for commits
app.editing(id, true/false);         // echo suppression while the user holds a control
app.gesture(id, "start");            // undo bracket: open at pointer-down...
app.push(id, value, {gesture:"end"}); // ...and close on the drag's FINAL push
```

**Gestures = undo honesty.** Bracket every continuous drag (`gesture "start"` at
pointer-down, `{gesture:"end"}` on the final push): every push inside the bracket is
ONE Grasshopper undo record, however slowly the hand moved. The kit binders already do
this; a custom re-skin must too, or slow drags fall back to time-gap coalescing and can
split. Discrete kinds (toggle, value list, colour, panel) record one undo per push and
need no brackets.

Push value shapes (the server refuses mismatches honestly): slider/knob = number,
panel = string, toggle/button = boolean, valuelist = index number or item name,
mdslider = `"x,y"` string (axis order per `state.axes`), colour = hex string.

Contract fine print:

- **The definition must be the FRONT Grasshopper tab to interact.** Grasshopper solves
  only the front document, so a value pushed at a background tab would sit unsolved with
  its downstream outputs empty — the server refuses instead (409,
  `code: WIREIFY_DOC_NOT_ACTIVE`), and `isActiveCanvas: false` on the frame says so
  BEFORE any drag. The kit's pill reads "not in front" in that state, and a refused push
  marks the control it came from ("not applied") and snaps it back to the canvas value —
  a change that did not land must never look applied. Reads and the stream keep flowing
  from the background; only pushes wait for the front tab. A raw page must do the same:
  switch the pill on `isActiveCanvas`, and revert the widget on a refused push.
- **`docUnits` and `tolerance` ride every frame** — the Rhino document's unit system name
  ("Millimeters", "Feet") and absolute tolerance. Label axes and report numbers from
  them; never let a script plumb units through as a view.
- **Every values POST answers one envelope** — `{control, clamped}` — including
  marker-only gesture bodies (those add a `gesture` echo). `(await r.json()).control`
  is always there.
- **MD sliders speak domain values everywhere** — `axes[].value/min/max` in the state
  and the `"x,y"` push are all in the axis intervals' own units; out-of-range pushes
  clamp (the receipt flags `clamped`). On an mdslider the scalar
  `value/min/max/step/decimals` are `null` — `axes` is the truth; never render a
  numeric field a kind does not carry.
- **Axis bounds are normalized** (`min <= max`) whatever direction the canvas interval
  was authored in — a widget can rely on it, and a page can therefore never recover the
  canvas pad's own axis direction from the frame.
- **A number pushed to a value list is ALWAYS an item index**, never matched against
  item value expressions; push the item's name as a string to select by name.
- **Colour accepts** `#rgb`, `#rgba`, `#rrggbb`, and `#rrggbbaa`; state reports
  `#rrggbb` with the alpha byte appended only when not opaque — alpha round-trips.
- **Expression sliders write raw and read raw**: `value`/`min`/`max` are the slider's
  own numbers (pushes are idempotent), and `evaluated` carries the expression's result
  for display. Show both — the kit's slider renders `raw = evaluated`.
- **`kind: "missing"` is a first-class kind**: the declared param left the document
  (undone away, deleted); it may return on a later frame. Render it honestly — the
  kit's `mount` shows a muted note; never a dead widget.
- **Views carry `id`, `param` AND `label`**: `id` is the declaring component's guid and
  `param` the bare addressable name (together they are exactly what `api/geometry` takes;
  a page needs neither its manifest nor a lookup to fetch a view's geometry); `label` is
  the qualified display string (`<component nick> <param>`). Match on `param`, title with
  `label`. A view whose component left the document arrives with `tree` empty and a
  `warnings` entry naming the id and the recovery (`missing from the document — <id> left
  the canvas (ctrl-Z in Grasshopper restores it)`); render the warning, keep the row.
- **Samples are a sample unless you ask for more.** A view delivers up to 5 samples per
  branch (50 total) by default while `tree.dataCount` reports the truth; when the list
  is short the frame says so with `samplesTruncated: true`. A real table or chart
  declares what it needs: `{"id": "...", "param": "...", "samples": 200}` (clamped to
  500) — big counts ride EVERY solve's frame, so opt in per view, not everywhere.
- **The state GET is wrapped** — `{...state, wireify: "<version> build <stamp>"}` — so
  a page can prove which build answers it; SSE frames carry the bare state.
  `docName` has the `.gh` extension trimmed — a page that wants "name.gh" adds it back.
- **The server emits `solving: true` at EVERY SolutionStart**, fast solves included —
  debouncing is the client's job, and the kit does it for you (250 ms before the pill
  flips to stale; the SolutionEnd state frame clears it). A raw page that renders the
  frame directly will flicker on drags, by its own choice.
- **The event stream uses NAMED SSE events** — the wire carries `event: state` and
  `event: status`, so `EventSource.onmessage` fires never and silently. Subscribe with
  `es.addEventListener("state", ...)` / `addEventListener("status", ...)`; the kit
  already does.
- **A control's page name comes from the manifest.** `"label"` on a controls entry is
  the human name the page shows ("ring radius (m)"); the canvas nickname stays visible
  beside it in parentheses, because the raw name is what a Grasshopper user maps a
  slider by. `"help"` is one line under it. Both ride the frame on the control (`label`,
  `help`, null when undeclared); `mount` renders them and re-renders them every frame,
  and a custom page should too. Write them for any page someone other than its maker
  will read: a nickname alone is a variable name, not a label. An integer-rounded slider
  (step 1, or 2 for even/odd) reads as an integer, as it does on the canvas.
- **Manifest mistakes arrive as `warnings` on the frame** — declarations the parse
  could not use (an `id` that is not a component guid, a missing `id`), keys a section
  does not take (controls take `id`, `label`, `help`; views take `id`, `param`,
  `geometry`, `samples`), a `label` or `help` that is not a non-empty string, and exact
  duplicates (the second entry is dropped) are named there instead of ignored silently. `mount` renders
  them wherever it ran — in the controls slot, else the views slot, else the top of the
  page; a page that skips `mount` must render `state.warnings` itself, and the fixture
  renders them on its own raw path (it does not exercise the kit's). An `id` must be the
  component's InstanceGuid; the output name goes in `param`. Fix the manifest and the
  warnings clear on the next frame.
- **Undo lives in Grasshopper, not the page.** A bracketed drag becomes ONE GH undo
  record — ctrl-Z on the CANVAS reverts it; ctrl-Z in the browser does nothing, and the
  page should not pretend otherwise.
- **Failure has honest statuses, and the kit switches on the `code`, never on the
  wording.** Every error body and every `status` frame that carries a protocol state
  names it in `code`; the sentence beside it is written for the page's reader and names
  the app's own file (never the MCP session's "this session is connected to …"). A raw
  page must classify the same way, or every failure collapses into "is Rhino open?" —
  which is exactly wrong three times out of four:

  | `code` | where | means | the kit |
  |---|---|---|---|
  | `WIREIFY_DOC_NOT_OPEN` | 409 on `api/state`, a `status` frame when the definition closes (or is saved under a new name), 409 on a push | the definition this app belongs to is not open at its path | "definition closed", polls, heals on reopen with no F5 |
  | `WIREIFY_DOC_NOT_ACTIVE` | 409 on a push | open but not the front Grasshopper tab | "not in front", the control marked and snapped back |
  | `WIREIFY_APP_UNAUTHORIZED` | 401 | the token rotated with the Rhino run | the expired-link message; the poll stops (nothing heals it) |
  | `WIREIFY_APP_NO_MANIFEST` / `WIREIFY_APP_BAD_MANIFEST` | 404 | `app/manifest.json` absent / unparseable (the parser's line in the body) | the body's sentence, polls, heals when it parses |
  | `WIREIFY_APP_UNDECLARED` | 403 | the page asked for a param or view the manifest does not declare | the body's sentence (a page-authoring error) |
  | `WIREIFY_APP_BAD_REQUEST` | 400 / 409 | a shape mistake in the request, or a refusal with no protocol code | the body's sentence |

  Only a fetch that cannot reach the server at all earns the generic "disconnected"
  line. Get a fresh link from the socket's Open app, Build, or `get_app_info`.

`mount(app, document)` renders every declared control and view with the default skin
into `[data-ify-status]` / `[data-ify-controls]` / `[data-ify-views]` slots and keeps
them reconciled (a control undone away on the canvas stays as a `missing` row showing
its id until it returns or leaves the manifest — never a dead widget). For a custom
layout, skip `mount` and use the per-kind `bind.*` helpers — each takes your elements
and returns the per-frame updater — or go fully custom on `app` alone.

### Custom controls: what a page must re-adopt

`mount` does more than draw widgets. A page that calls it for the pill alone (or not at
all) and renders its own controls keeps syncing and quietly loses everything else `mount`
did for it — the failure mode is always "looks fine, does less", and three test rounds
filed those gaps as product defects before the pattern was named. Use `bind.*` per kind
and inherit them, or re-adopt each one:

- **The refused-push mark and snap-back.** `app.onStatus` carries `id` on a refusal
  (`kind` "background" for the front-tab rule, "blocked" / "error" otherwise): mark that
  control, re-apply `app.control(id)` to its widget so it shows the canvas value again,
  and clear the mark on the next frame that reports `isActiveCanvas !== false`.
- **The pill vocabulary**, "not in front" included (`kind: "background"` arrives before
  any drag), and the codes the recovery classifier switches on (the table above) —
  classify by `code`, never by the sentence's wording.
- **The bake status sentence.** `bake()` returns `status` beside `size` and `warnings`;
  render it as is — the page cannot see what the browser did with the download.
- **The gesture bracket** on every continuous drag, or slow drags split into several undo
  records.
- **`missing` and unsupported kinds as first-class rows** — the id shown, never a dead
  widget (controls carry `kind: "missing"`; views carry the id in `warnings`).
- **Prose direction.** A panel's payload is text; the kit's textarea and text set
  `dir="auto"` so right-to-left content lays out against its own base direction. A page
  rendering its own panel widget does the same.
- **The warnings strip.** `mount` renders `state.warnings` wherever it ran; a page that
  skips it renders them itself.

Derive, don't restate: bounds, items, and step always come from the state frame, never
hardcoded in markup — a canvas edit re-shapes the app on the next frame.

### Running a page without Rhino (a showcase)

A page can run with no server behind it when its data layer is replaced by a local object
that speaks the surface `mount` and `bind.*` use (`homeId`, `state`, `onState`, `onStatus`,
`control`, `editing`, `isEditing`, `gesture`, `push`, `close`) and solves the definition's
logic in the browser. What one such port taught, so the next one is shorter:

- Copy `kit/` verbatim and change only the page's import and its one connect call; the
  page keeps `mount` and the widgets, and only the data layer is new.
- Do what the server does with the manifest: read `label` and `help` and attach them to
  the controls before emitting (a `setControlText(map)` on the local object), so the kit renders the
  human names.
- `docUnits` on the frame is the page's unit word; label the intended unit there and leave
  the solver's text rows as they are, so a parity check against the canvas stays exact.
- Log a listener failure with its message in the string, not only the Error object; a
  browser console tool shows the latter as "Object".
- Keep the solver's numeric core untouched when the page changes, and prove it: a digest
  of every view plus the geometry's item counts and bounds over a handful of saved states,
  taken before and after, must be identical. Geometry extras (`radius`, `markers`, `color`)
  live outside that digest.
- The embed contract with ifylab.dev: `?embed=1` hides the page's own chrome and
  `postMessage({type: "ify:theme", mode})` from the site's origin flips the theme.

## The 3D viewport (`kit/ify-viewport.js`)

Geometry renders locally from vendored three.js (`kit/vendor/`, no CDN). Two steps:

1. Mark the view in `app/manifest.json`: `{"id": "...", "param": "L", "geometry": true}`
   — only opted-in views serve meshes (`api/geometry` refuses the rest).
2. Add the import map (before any module script), then bind:

```html
<script type="importmap">
  {"imports": {"three": "./kit/vendor/three.module.min.js"}}
</script>
```
```js
import { bind as bindViewport, create } from './kit/ify-viewport.js';
const bound = bindViewport(app, viewId, canvasContainerEl, { param, statusEl, viewsEl });
bound.viewport.fit();             // re-frame at the current angle; refresh() forces a fetch; dispose() tears down
bound.viewport.lookFrom('top');   // 'iso' | 'top' | 'front' | 'side' — the buttons in viewsEl do the same
```

`bind` fetches `api/geometry` after every state frame (debounced — a drag collapses to
one trailing fetch) and renders meshes with their crease edges, sampled curves, and
points, Z-up like Rhino. The payload is budgeted server-side (items taken whole or
skipped whole) and `statusEl` gets the honest line — rendered/total counts and every
warning. Fit frames the model's own vertices and re-centres on the projected
silhouette, so the same payload frames identically on every load. After the first fit the
camera holds still while the user holds a control - what moves on screen under a drag is
the model's own size, not the zoom - and settles once the hand comes off, at the current
angle, only when the model has left the frame or shrunk deep inside it. `bind` wires that
hold to the app's `editingAny()`; a page with its own data layer passes its own
`holding: () => boolean` to `create`. A camera the user is orbiting is left alone.
`viewsEl` (a `.ify-viewport__views` span in the head) receives the named views — 3D,
top, front, side — and fit; when the first geometry lands a translucent card with the
orbit glyph and the gestures (drag to orbit, right-drag to pan, scroll to zoom; touch
wording on coarse pointers) sits over the canvas for two seconds or until the first grab,
and the first framing eases in from a few degrees around the model's axis so a still page
shows a model that turns (`hint: false` and `intro: false` skip them; reduced motion skips
the motion). An item in the payload may carry `color` — a CSS colour or a
`--token` name, re-resolved on a theme flip — to be drawn apart from the rest (a member
family, a flagged clash); a curve with a `radius` draws as a thin tube instead of a
one-pixel line; a `markers` list (`{at: [x, y, z], radius, color}`) draws spheres on the
model outside the item counts (a clash point, a support). The live `api/geometry` carries
none of these today; a page that builds its own payload (a standalone port) can. The interior otherwise reads exactly four
tokens — `--ify-viewport-ground/-grid/-mesh/-edge` in `kit/ify-tokens.css` (aliases of
the theme today); re-theme those in `theme.css`, never the module. The interior look is
provisional; the module API and payload shape are the stable part.

## The baked report (`kit/report/`)

The explicit export step: one self-contained HTML file, frozen at capture, opens
anywhere with zero network. Offer it as its own action ("want a shareable report of
this state?") — never as "the app without Wireify" (independence with working compute
is a different product).

```js
import { bake } from './kit/report/ify-report.js';
const { size, warnings, status } = await bake(app, { notes });  // downloads the file
statusEl.textContent = status;  // "report generated (0.2 MB) — check the browser's downloads"
```

Mark what to capture with `data-ify-bake="caption"`: a viewport container becomes a
JPEG (1600 px, q0.85), an SVG chart inlines losslessly, a table carries over. The bake
adds the inputs-at-capture and outputs tables itself, embeds the fonts, hard-bakes the
current theme, and banners the file as static. Surface the returned `warnings` (size
past ~5 MB, skipped captures) — never drop them.

The bake **flattens to ONE theme** — the file carries no theme toggle and no
`prefers-color-scheme` blocks, deliberately: a report handed to a client must not
invert with the reader's OS. That makes the appbar's Light/Dark toggle a BAKE-TIME
decision — set the theme you want the record in, then bake; bake once per theme when
both are needed. And the page can only report that it GENERATED the file — whether the
browser kept the download is outside its sight, so show the returned `status` sentence
(it says "check the browser's downloads") rather than writing your own "saved".

Testing a bake twice in one session: Chrome silently drops a second programmatic
download. Capture the file instead of downloading it — patch `URL.createObjectURL` to
stash the Blob and the anchor's `click` to a no-op before calling `bake`, then read the
Blob's text; that is how the shipped report contract is verified repeatedly.

## Page templates

`scaffold_app` seeds `index.html` from a template when none exists (never overwrites):

- `template: "panel"` (default) — the control-panel starter: inputs grid + output cards.
- `template: "report"` — the live-report shape: hero viewport, KPI strip (single-number
  views), output tables, a controls drawer, and the save-report action. The shape for
  "present my definition" asks.

Both are starting points the agent reshapes; the kit look itself is only the default.

## From a prompt to a page

For an ask like "a live report presenting my definition with geometry, charts, and
tables":

1. **Views are a conversation, never auto-picked** — ask which outputs matter (the
   geometry worth watching is often produced inside a script component; wire it to a
   named output first). Controls are inferable from the staging; views are not.
2. Scaffold with the shape that matches the ask (`report` for presenting, `panel` for
   tweaking), declare the chosen views — geometry views with `"geometry": true`, and a
   per-view `"samples": N` wherever a table or chart needs real rows.
3. Compose the sections: KPI readouts for single numbers, `chart.bars/line` where a
   series tells the story, `.ify-table` for real tables, the viewport for the model.
   Derive everything from the state frame; restate nothing.
4. **Names are the page's copy.** Component nicknames and param names render verbatim
   on cards, KPI labels, and report column headings — name components at create
   (`create_python_component` takes `nickName`) and params at `set_io` for what they
   carry; `x`/`y`/`in1` on a page read as placeholder text.
5. Verify the page before presenting it (next section), then offer the baked report as
   the shareable artifact of a state worth keeping.

## Verify in the browser

The page you just built or edited is the one artifact you cannot see from the terminal
— and a page can be completely dead while every tool receipt reads success. When
browser tools (Claude in Chrome) are available, verify before you present:

1. Open the app URL from `get_app_info` (Wireify homes pre-allow the browser
   verification toolset in their scaffolded settings — no Claude Code permission
   prompts; Chrome itself asks ONCE to connect the extension, a browser-level grant no
   settings file can or should pre-answer).
2. Gate on identity first: the state GET's `wireify` build (the fixture's meta line
   renders it) — you are looking at the build you think you are.
3. Check what receipts cannot tell you: sections render (empty states where empty,
   never headings over a void), the pill reads live with the definition's tab in front
   (and "not in front" when it is not), `api/state` answers `warnings: null` (the
   `manifest:` strip renders only where `mount` ran, so read the frame, not just the
   DOM), the console has no errors, the viewport actually frames the model, and one
   control drag moves what it should. `javascript_tool` is pre-allowed for reading
   `api/state` from inside the page when the rendered DOM is not evidence enough.
4. Close the tabs you opened (`tabs_close_mcp` is pre-allowed) — tabs you create are
   yours to clean up.

When browser tools are NOT available, say so plainly and hand the user the URL with
that same checklist — never imply the page was verified when it was not.

### Probing without a browser

The API needs no browser at all. Reserve Chrome for when the RENDERED page is the thing
under test; everything else — values, frames, geometry counts — reads faster and more
reliably through `node` (blocked PowerShell networking does not imply blocked loopback):

```js
// probe.mjs — node probe.mjs <app url from get_app_info>
const [url] = process.argv.slice(2);          // http://127.0.0.1:<port>/app/<home>/?token=…
const base = url.replace(/\/\?token=.*$/, "/api/");
const token = new URL(url).searchParams.get("token");
const h = { cookie: `wireify_app=${token}` };  // the cookie the entry link would have set
const state = await (await fetch(base + "state", { headers: h })).json();
console.log(state.wireify, state.isActiveCanvas, state.controls.map((c) => [c.nickName, c.value]));
await fetch(base + "values", { method: "POST", headers: { ...h, "content-type": "application/json" },
  body: JSON.stringify({ id: state.controls[0].id, value: 8 }) });
// Frames: named SSE events — count them here, not in the page (a page counter dies with
// any reload). EventSource cannot send the cookie; use fetch + the raw stream instead.
const res = await fetch(base + "events", { headers: h });
for await (const chunk of res.body) process.stdout.write(new Date().toISOString() + " " + chunk);
```

Browser-harness facts that cost real time before they were written down:

- Named SSE events (`event: state`) — `onmessage` never fires. `addEventListener`.
- Never sample a page with `setInterval`: a backgrounded tab throttles timers to about
  one a minute, which reads as "the page never updated". Use a `MutationObserver`, or
  poll from `node`.
- A backgrounded tab does not run `requestAnimationFrame`, so canvas reads return a
  STALE framebuffer and `await requestAnimationFrame` never resolves. Force a paint (a
  screenshot) right before measuring pixels.
- A Chrome tab id rotates on any navigation or self-reload; stale ids fail in several
  misleading ways (a "permission" error included). Re-read the tab list after anything
  that could reload.
- `computer` click coordinates are screenshot space, already scaled — verify once with
  a capture-phase click listener before concluding a control is unwired.
- The `javascript_tool` drops non-string returns (`{}` even though the code ran and its
  side effects happened): return a joined string, or stash to a global and read it back.
- Its content filter blanks whole results as `[BLOCKED: …]` on innocuous strings that
  contain quoted `id` text. Cross-check any suspicious value through `node` before
  blaming the server.
- Clicking a slider's grip on the canvas is a value-set gesture in Grasshopper itself:
  it expires the slider on mouse-up even at an identical value, so two frames for a
  real solve are honest, not chatter. A click that only selects the capsule emits none.
- The extension itself can disconnect mid-session ("Browser extension is not connected").
  Unlike a stale tab id, re-reading the tab list does not recover it: reconnect the
  extension first, then re-read the tabs.

## The variant ladder

When the user wants a different look, pick the cheapest rung that answers the ask:

1. **Re-theme** — redefine tokens in `theme.css`. Colors, faces, spacing register.
2. **Re-skin** — keep `connect`/`bind`, replace the presentation (a slider drawn as a
   dial or an illustration, a bespoke layout). The binding core stays untouched.
3. **Re-shape** — a new page entirely, still on the core and the API contract.

## Class reference (kit/ify-app.css)

| Root | What |
|---|---|
| `.ify-app`, `__main`, `__body` | page shell; body = side + main grid, collapses under 860px |
| `.ify-appbar`, `__mark`, `__doc`, `__ext`, `__right` | sticky header; the DEFINITION is the title, ify is the mark |
| `.ify-tabs`, `.ify-tab`, `--on` | page tabs |
| `.ify-status`, `--live/--stale/--blocked/--down`, `__dot` | the status pill (vocabulary below) |
| `.ify-toggle` | theme toggle pill |
| `.ify-section`, `__title` | heading with its trailing hairline |
| `.ify-controls`, `__wide` | the 3-column control grid; wide rows span it |
| `.ify-slider`, `__text/__name/__nick/__help/__bounds/__input/__value` | slider + knob rows; nick = the nickname beside a manifest label, help = the line under it, bounds = the range (the step is the input's business, not the reader's) |
| `.ify-pad`, `__thumb`, `__value` | the MD-slider 2D pad |
| `.ify-check`, `.ify-select`, `.ify-input`, `.ify-color` | toggle, value list, panel edit, colour |
| `.ify-btn`, `--primary/--secondary/--ghost/--sm`, `.ify-hold` | buttons; `.ify-hold` is the momentary kind |
| `.ify-readout`, `.ify-panel`, `.ify-table`(`__wrap/__trunc`), `.ify-list` | outputs |
| `.ify-viewport`, `__head/__label/__action/__views/__hint/__canvas` | the 3D frame; views = the named-view buttons, hint = the interaction line (interior contract comes later). An `.ify-fine` inside the frame renders as its foot - flush under the canvas, mirroring the head - so counts and warnings read as chrome, not prose |
| `.ify-chart` | SVG charts via `chart.bars/line` |
| `.ify-empty`, `.ify-card`, `.ify-badge`, `.ify-kicker`, `.ify-fine`, `.ify-wordmark` | the rest |

## Design rules (non-negotiable)

- **App density, not site density.** This page sits beside a Grasshopper canvas. The
  register is already encoded in `--ify-app-gutter/-bar-y/-gap` — do not widen toward
  document rhythm.
- **Four faces, four jobs.** Jost light = structural chrome only (never content
  headlines); Hanken = body and titles; Fragment Mono = every label, pill, value, date,
  caption. Uppercase belongs nowhere; section headers use the title + hairline idiom.
- **Depth from spacing and hairlines, never shadow. Hover by color or border only.**
- **Pills are not buttons.** The status vocabulary is closed: `--live` (calm accent
  band), `--stale` (solve in flight or transient), `--blocked` (warm = the definition is
  not the front tab, or a push the server refused — the text names why), `--down`
  (outline: honestly off — closed, expired, unreachable). "Not in front" is a normal
  state, said before any drag; a refused push additionally marks its own control.
- **Native controls stay native** in the default skin — custom-drawn controls belong to
  deliberate re-skins the user asked for, not to defaults.
- **Lists and tables over card grids.** Cards are a last resort, never nested.
- **The wire is spent in one zone at most**: the footer provenance line. No wire
  decoration anywhere else, ever.
- **Honesty in pixels**: truncation labeled (`.ify-table__trunc`), empty and error
  states rendered (`.ify-empty`), refused pushes surfaced in the pill — never a dead
  control pretending to work, never an empty viewport as a failure state.
- No emojis. No gradient text. No glassmorphism. Nothing overflows its container —
  wide content scrolls inside its own wrapper.

## Runtime rules

- Never push to a param the manifest does not declare — the server refuses, by design.
- Value pushes need the definition to be the FRONT Grasshopper tab, exactly like every
  tool mutation: Grasshopper solves only the front document, so a value landing on a
  background tab would sit unsolved with its downstream outputs empty — the server
  refuses (`WIREIFY_DOC_NOT_ACTIVE`) and the page says so on the pill before any drag
  and on the control after a refused one. Reads and the live stream keep working from
  the background; a CLOSED definition (or a gone Rhino) is the other stop, and the
  recovery classifier says which. Never tell a user their slider "snapped back" is a
  defect: it is this rule, and the fix is one click on the definition's tab. ctrl-Z
  lives in Grasshopper too.
- Surface `closed` and refusal states plainly; they are normal states, not errors to
  hide.
- The app URL rotates every Rhino run. Links come from the socket's Open app, Build's
  console line, or `get_app_info`;
  never write a token into a committed or shared file.
- Homes are not repos, but users copy things: keep the page self-contained (relative
  paths only, no external fetches).
- The blue **wireify capsule** on canvas objects is the document's own record of what
  the agent created or wrote (inert core-GH data — the file opens stock without
  Wireify). App-page drags never badge (a browser drag is the user's own hand). It is
  permanent by default; `clear_badge` is the deliberate exit — not an undo record,
  persisted at the user's next save, re-badged by any later touch. The `W<n>` numbered
  capsule follows the component's nickname instead; renaming drops that one.
- `hidden` on any kit element hides it (`[hidden]` outranks the kit's display rules): a
  page that cannot offer an action removes or hides its control — a dead button that
  still looks live is a lie in pixels.
