# Changelog

All notable changes to Wireify are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer.

## [0.3.0] - 2026-09-16

The companion web app. Every definition can now carry a browser page the plugin serves on
your own machine, in step with the canvas both ways: sliders and panels on the page push into
Grasshopper, every solve pushes back into the page's 3D view, drawings and figures, and one
button bakes a frozen HTML report. Claude builds the page from the definition; opening it
needs no Claude session. Around it: 22 canvas tools including a whole-definition wiring
graph, rename and layout tools, a socket plate with Build and Open app, per-file panel state,
and arguments checked against each tool's schema before anything runs.

**Upgrading from 0.2.0:** 0.2.0 no longer connects to current Claude Code (the MCP protocol
moved under it); the Package Manager updates existing installs on their next Rhino start.
Rhino 8 SR18 or newer on Windows; Mac support is planned; Rhino 7 is not supported.

### Added

- Companion-app pages can name their controls: a control entry in `app/manifest.json` takes
  `label` (the human name the page shows, with the canvas nickname kept beside it) and
  `help` (one line under it); both ride every state frame and the kit renders them, and an
  integer-rounded slider reads as an integer, as on the canvas, and a slider row shows its
  range without the step. The 3D viewport gained named views (3D, top, front, side) beside
  fit, an opening turn with a two-second gesture card (reduced-motion aware), per-item
  colours in a geometry payload, curves with a radius drawn as thin tubes, and a `markers`
  list drawn as spheres. Its camera holds still while a control is held, so what changes
  size under a drag is the model rather than the zoom, and eases to a new framing at the
  current angle when the hand comes off, only if the model has left the frame or shrunk
  deep inside it. A status line inside a viewport now renders as that frame's foot; the
  slider column keeps its width beside long labels, and `hidden` hides any kit element. A
  page can also drive the camera along a path of its own: `fly(legs)` moves through a list
  of positions with per-leg easing and `poseFor(name)` hands back a named view's exact
  framing, so a walkthrough can end on the view it hands back to; a grab cancels the flight
  and reduced motion lands on the last leg. A control's help line takes its own row across
  the control grid, with the canvas nickname and the range on one quiet line above it;
  table headers align with the cells they sit over, and below 560px a table drops its
  header row and each row becomes a labelled block.
- Companion-app surface: the local server can now serve a per-definition browser app from the
  home's `app/` folder (`/app/<home-id>/`) with a live API underneath it
  (`/app/<home-id>/api/state`, `values`, `events`) — a server-sent-events stream pushes fresh
  state after every completed solve, and the browser pushes values back into the canvas
  (number sliders, panels, boolean toggles, and value lists), so the app and the definition
  stay in step whichever side moves. A slider drag coalesces into a single undo step. Each
  home gets its own per-run access token (the MCP credential never appears on this surface);
  the entry link carries it once and a strict session cookie takes over from there, so the
  token never lives in the URL. The server touches only params declared in the home's
  `app/manifest.json`, interacts only while the definition is the front Grasshopper tab
  (Grasshopper solves only the front document, so the page says "not in front" and a change
  made there is refused and marked on its control rather than parked unsolved; reads and
  the live stream keep flowing from the background), and ends the stream with an honest
  status message when the definition closes. A new `get_app_info` tool hands a session its
  app link. Using the app needs no Claude session: the plugin serves the page and reads the
  definition on its own, so opening the `.gh` in Rhino and pressing Open app is the whole
  gesture (a page served before any Build could not read its document during testing — the
  app now resolves its definition by the file path its home is keyed to).
- Three more app-control kinds, on the canvas and in the app: the MD slider (a 2D point
  pad in the browser), the colour swatch (a native color picker), and the dial knob (a
  slider-shaped control with its own decimals grid). All three read and push over the same
  declared-params contract, drag-shaped pushes coalesce into single undo steps, and
  `create_control_component` can place them (eight kinds total).
- `scaffold_app` tool: raises a session's companion-app folder in one call — stamps the
  bundled `.ify` app kit into `app/kit/` (design tokens, component styles, three OFL-licensed
  font files with their NOTICE, and a headless sync/binding library the page builds on), and
  seeds a styled starter page, an empty manifest, and a user-owned `theme.css` only where
  those files are absent. Kit files are Wireify's and refresh at every Build; the page,
  manifest, and theme belong to the user and are never overwritten. Multiple open tabs of one
  app now share a single live stream. `app/kit/KIT.md` documents the classes, the binding
  API, and the design rules.
- 3D viewport in the companion app: a view declared with `"geometry": true` serves its
  geometry at `api/geometry` — breps, surfaces, and boxes meshed tool-side with the fast
  render preset, curves sampled to polylines, points passed through — and the kit's new
  `ify-viewport.js` renders it locally with vendored three.js (orbit, fit-to-bounds,
  Rhino's Z-up; no CDN, nothing leaves the machine). Payloads are budgeted honestly:
  items render whole or are skipped whole, and the page is told exactly what was capped.
- Baked HTML report: an explicit export step in the app (`kit/report/`) that saves ONE
  self-contained file — the input values at capture, the watched outputs, viewport
  captures as images, charts inlined — labeled as a static record, fonts embedded, theme
  hard-baked, zero network requests to open. The live app stays the live thing; the
  report is a frozen snapshot by design.
- A second page template: `scaffold_app` takes `template: "panel"` (the control-panel
  starter, default) or `template: "report"` (a live-report shape — hero viewport, key
  figures, output tables, a controls drawer, and the save-report action) for
  present-my-definition asks. An existing page is never replaced.
- Per-gesture undo brackets: the app can mark where a drag begins and ends
  (`gesture: "start"`/`"end"` on the values contract), and every push in between amends
  ONE undo record — a slow, deliberate drag is one ctrl-Z, exactly like a fast one. The
  kit's widgets send the brackets automatically; pages that do not still get the
  quiet-gap fallback.
- The state response now carries the definition's display name and the answering build's
  version, expression sliders report their evaluated value beside the raw one (pushes
  read back exactly what they wrote), and the raw-contract fixture page ships in the kit
  so every install carries a known-good reference app.
- `delete_component` now also removes native app controls (the eight kinds
  `create_control_component` places), one undo record with wires included.
- Lesson gate: a scaffolded Stop hook now backs the loop's record-the-lesson step. When a
  session built or rewired components but the definition's `MEMORY.md` gained no new lesson,
  ending the session is blocked once with a reminder (answering that nothing durable emerged
  is accepted). Three small per-OS scripts under the home's `.wireify/hooks/` do the
  checking — every check is a local file test; no model calls, nothing transmitted.
- The Wireify canvas badge: objects the agent creates or writes carry a small blue
  `wireify` capsule on the canvas, so what a session added is visible at a glance. The
  record is plain document data (Grasshopper's own value table): it saves only when you
  save, survives rename and reopen, and a badged file opens stock without Wireify. Drags
  from the app page never badge — a browser drag is your own hand. A new `clear_badge`
  tool removes one object's record deliberately (tool #20).
- Views name themselves properly: every view carries `param` (the bare name the manifest
  declares — match on this) AND `label` (the qualified display string), a view can declare
  `"samples": N` (up to 500) when a real table or chart needs its rows, and a sampled
  list says so (`samplesTruncated`, with the true count beside it). Manifest entries the
  parse cannot use are never dropped silently: they arrive as `warnings` on every state
  frame (and on `get_app_info`), naming the entry and the fix.
- The agent verifies its own pages in the browser: scaffolded homes pre-allow the Claude
  in Chrome verification toolset, `app/kit/KIT.md` carries the verify flow (identity gate
  first, then what receipts cannot tell you), and when browser tools are absent the agent
  says so and hands over the URL with the checklist instead of claiming a check it never
  made. Chrome itself still asks once to connect its extension — a browser-level grant no
  settings file can pre-answer.
- Two bundled frontend skills: `wireify-frontend` (the design half of the companion app —
  propose the page's shape, views are a conversation and never auto-picked, the `.ify`
  design language and dataviz-honesty rules, static-by-default with a named React
  escalation path) and a vendored `frontend-design` (Apache-2.0, from anthropics/skills
  via skillmeld discovery — provenance and license text ship with it).
- Naming reaches the app: `create_python_component` takes a `nickName` (component names
  are user-facing — the app renders them on every card and report heading), `scaffold_app`'s
  receipt reports what it DID (`seeded` vs `skipped` files, so the agent relays the truth),
  and Build prints the app link for every live home — scaffold hint included — into the
  console and the connect log.
- Both page templates put the controls in a left rail beside what they drive (the panel
  starter beside the outputs, the report's adjust drawer beside the hero viewport, sticky
  under the app bar), collapsing to one column with controls first on narrow screens —
  the live report's point is move-something-watch-it-answer.
- Inputs can be renamed without losing their wires: an input spec's `renameTo` gives the
  final variable name while `name` keeps addressing the staged input (`convert_staged`)
  or the current param (`set_io`) — so a socket input left as `in1` converts as `lines`
  with its wire intact, and the tools now treat shipping generic `in1`/`in2` names on a
  built component as a mistake to fix, not a default to keep.
- Agent-created objects land like an engineer placed them: controls anchored near a
  component form a vertical column at its left. With `nearInput`, each control sits
  flush on the row of the input it feeds — the Extract Parameter convention, one control
  per input row with no gap between them — and a bare `nearId` stacks below whatever
  already occupies the spot, so four controls for four inputs are four rows, never a
  pile. From-scratch components get the same collision-aware placement instead of a
  fixed cascade that could land on user objects. Known in this release: at Grasshopper's
  20 px input pitch the `wireify` badge of a row-anchored control (drawn above its object)
  lands on the control above it and can cover that control's value — `clear_badge`
  removes it, and a badge that sits beside its control is queued for the next release.
- `get_document_graph` tool (tool #22, 22 tools total): the definition's wiring in one
  read — every component and floating param with its type identity (component guid,
  category, subcategory) and its params, and every wire as an edge list. `includeOutputs`
  inlines each output's live data at small caps; `ids` scopes the read to a component and
  its neighbours; whole-canvas reads are bounded like the summary. Reading a definition's
  wiring used to cost one `introspect_component` per component (36 serial calls, then 32
  more for values, before one real port could start).
- `rename_component` tool (tool #21, 21 tools total): rename any canvas object by id as
  one undo step. Nicknames are the page's copy — the companion app shows a control's
  nickname on its card and the report prints it as the row label — and until now an
  unnamed Number Slider read as an anonymous "slider" on every page with no way for an
  agent to fix it short of a hand edit in Grasshopper. Sockets are refused (they are
  addressed by number); renaming a converted `W<n>` component off its prefix drops its
  number, and the receipt says so. `clear: true` un-names deliberately — the card and
  the report row read by kind again — so an agent can take back its own naming on a
  saved file without the user's hands.
- The tools now say more of what an agent otherwise had to see or guess:
  `introspect_component` returns each object's canvas `bounds` and each param's `gripY`
  (the object is laid out on demand before reporting, so the numbers hold even while the
  Grasshopper window is not repainting — placement is checkable numerically, not by
  screenshot), `get_document_summary` reports
  `unsavedChanges` and `savedAt` (an unsaved document is not drift), and the app's state
  envelope carries `docUnits` and `tolerance` (a page labels its axes, the baked report its
  numbers, without a script plumbing units through as a view).
- `bake()` returns the status sentence beside `size` and `warnings` — "report generated
  (N MB) — check the browser's downloads" — so a page never has to get the honest copy
  right on its own.
- Custom pages are told what they opt out of: a page that renders its own controls
  instead of the kit's `mount` keeps syncing and quietly loses the rest — the refused-push
  snap-back, the bake status sentence, the pill copy. `app/kit/KIT.md` lists exactly what
  such a page must re-adopt.
- The Wireify component grew a plate. Below the body, a few lines say what to do next —
  the prompt to type (`do #n: …` naming the wired inputs), each step of a build and any
  failure with its hint, a note after a Save As saying where the app stayed, and,
  once a page exists, the companion app's stable address — with two buttons under them:
  **Build** (opens the Claude terminal; **Session open**, inert, while one is live) and
  **Open app** (opens the page in the default browser with this run's link minted on
  click; **no app yet** until a page exists, and clicking it says what to do). The
  address carries no token and is never written into the `.gh`; clicking it copies the
  working link. Right-click adds New Claude session, Open companion app, Copy app link,
  and Open app folder; the Wireify panel gets the same Open app button. The plate hangs a
  clear gap below the body so the socket's zoomable last `+` stays whole.

### Fixed

- A renamed control placed against its component but not yet wired to it grew rightward
  over the input it was placed for: the rule that holds a control's right edge asked for a
  wire. It now asks for a neighbour — anything sitting immediately right of the control on
  its row, wired or not — and measures the edge after laying out.
- The panel followed the active definition only when it was opened or a Build ran, so a
  tab switch left it showing, and Open home acting on, the previous file. It now re-reads
  its rows, buttons and registry whenever another document comes to the front, and its
  Claude row reports that file's own session rather than the most advanced one anywhere.
- A whole-canvas `get_document_graph` overran the tool-result limit on a 50-object
  definition while reporting nothing truncated: the script params' 35-entry hint list rode
  on every typed param. Params are compact now, the hint registry is listed once, and a
  serialized-size budget truncates nodes honestly with the reason named.
- A Build with no Claude Code CLI on PATH still opened a terminal that printed "'claude' is
  not recognized" and idled, while the panel called the terminal step ok. The terminal is
  not launched, the step says why, and the full install hint reaches the panel log, the
  connect log, and the command line whoever pressed Build.
- `get_document_graph` dropped every wire that ends at a floating param — a panel or a
  relay param has no inputs in Grasshopper's terms; its incoming wire sits on the param
  itself. Nineteen of the truss's 198 wires were missing without a word. Every wire into
  a floating param is an edge now.
- `get_document_graph` still overran the tool-result limit on a 192-object canvas: the
  budget counted the nodes but not the edges, and each edge carried two 36-character ids.
  Edges address nodes by index, nodes carry no type guid, and the budget is estimated in
  tokens over the whole reply; the truncation note names the budget and what to do.
- Copy-pasting a staged socket kept the source's number, so two sockets read `W2` and
  `do #2` was ambiguous with nothing flagging it. A socket settles its number when it
  first solves — the first holder in document order keeps it, a paste takes the next free
  one — and `get_document_summary` warns whenever two objects carry one number.
- Wiring a control placed against its component widened it about 20 px into the gap.
  The right edge is now pinned when the control is created and again after it is wired.
- A tool call with a wrong parameter name, or a number where a uuid belongs, answered a
  bare "An error occurred invoking '<tool>'." The server checks arguments against the
  tool's own schema first and names the field, the expected shape, and the signature —
  and nothing runs.
- The socket's "Working" state is gone: a conversion replaces the socket before any
  repaint could show it, so three test rounds never saw it. The converted component
  appearing is the signal.
- A socket re-added by undoing its own conversion took the next free number because the
  converted component still held the old one for a moment; it keeps its number now and
  settles it once the transaction is over, so `do #2` stays `do #2`.
- After a Save As with both files open, the companion app rendered two documents at
  once: its live stream, pill and refusal text followed the Claude session to the
  renamed copy, while its state and geometry reads followed the file path to the
  reopened original. Every app channel now resolves the home's own file — the stream
  re-binds when documents open, close, or are saved under a new name, and reads
  "definition closed" until the original is open again — and a refusal names the app's
  file in page copy, never the session's sentence.
- One ctrl-Z after `rename_component` restored the name and the old position but kept
  the new width, so the capsule overshot the component it fed by the whole width delta
  (120 px over it, on the input it drove). The rename's undo now restores name, width and
  position together; redo restores the renamed layout the same way.
- The panel's step rows stayed `-` and Open home stayed disabled unless the panel itself
  had driven the Build. Both follow the active definition now: the rows show its last
  Build's steps whoever pressed Build, and Open home opens its home whenever the folder
  exists.
- On document close the status pill briefly read "not in front" and then the agent's raw
  `WIREIFY_DOC_NOT_OPEN` sentence before settling on the closed line. Status frames carry
  the protocol code, the kit classifies by code and never renders a protocol line, and a
  closing document no longer emits a tab-switch frame on its way out.
- The Build failure hint for a missing Claude Code CLI was one long line, macOS first,
  clipped at the plate's width; it is now segmented, host platform first, and fits.
- A missing or wrong app token answered 401 with the shape-mistake code; it now carries
  `WIREIFY_APP_UNAUTHORIZED`, and the kit's docs list every code a page switches on.
- The kit's status pill shows a supplied `text` for a live status, so a page can label
  its own live state.
- The agent loop died on current Claude Code versions (`/mcp` showing "connected · tools
  fetch failed"): MCP protocol revision 2026-07-28 requires a `resultType` field on every
  result, and the MCP SDK bundled with 0.2.0 predates the requirement. The SDK is updated and
  the server now answers current and older clients correctly, each in its own dialect. Until
  the update reaches an affected install, setting `MCP_PROTOCOL_NEGOTIATION=legacy` in the
  environment Claude Code starts from restores the connection with no reinstall.
- Companion-app requests now bind to the app's own definition instead of the front
  Grasshopper tab — a value push from the browser used to answer with the wrong file's
  refusal when another definition was in front.
- The companion app's live stream re-reads `app/manifest.json` on every solve, so a manifest
  edit shows up on the next recompute instead of surviving only a full close-and-reopen of
  every app tab.
- Type hints round-trip: `float` and `double` are aliases on inputs and outputs alike, so
  feeding an introspection result straight back through `set_io` is always accepted
  (outputs used to echo `float` and then reject it).

### Changed

- **Connect is now Build.** The socket's button, the panel, the console lines, and the
  docs say Build for the develop gesture (open a Claude terminal for this definition),
  beside Open app for the use gesture. The `_Wireify` command and the `connect-*.log`
  file names are unchanged.
- The status pill is driven honestly end to end: a solve's start marks the app stale
  (fast solves never flicker it), a Grasshopper tab switch pushes a fresh frame so
  blocked/live flips immediately, and MD sliders now speak domain values everywhere and
  clamp out-of-range pushes like every other kind.
- The companion app tells the truth about the front tab. Grasshopper solves only the
  front document, so a value pushed at a background tab would sit unsolved with its
  downstream outputs empty — the server refuses instead, the pill reads "not in front"
  the moment the definition's tab goes to the background (before any drag), and a change
  made on the page in that state is marked "not applied" on its own control and snapped
  back to the canvas value, never silently parked. The page keeps reading and streaming
  from the background; ctrl-Z lives in Grasshopper.
- The app page renders manifest warnings on custom layouts too: a page that mounts the
  kit for the status pill alone (no controls or views slot) used to get no `manifest:`
  strip at all — the strip now falls back to the top of the page. The manifest parser
  also warns on keys a section does not take (a `label` on a control did nothing, in
  silence) and on exact duplicates (the second entry is dropped), and a control whose
  component left the canvas shows its id instead of a bare "missing" — and so does a
  view, whose warning now names the id and the ctrl-Z recovery. Every view frame carries
  the component's `id` beside `param` (what `api/geometry` takes), so a page no longer
  has to carry it from its own manifest.
- The app API's error `code` carries the real protocol code where there is one —
  `WIREIFY_DOC_NOT_OPEN` on a closed definition, `WIREIFY_DOC_NOT_ACTIVE` on a
  background-tab push — instead of `WIREIFY_APP_BAD_REQUEST` for everything, so a client
  switching on the code no longer conflates a closed definition with a bad token. A home
  that no open definition matches answers `WIREIFY_DOC_NOT_OPEN` as well (it used to be
  the generic code, and the page called an open file closed), and the page's recovery
  probe switches on the code too.
- `rename_component` re-lays the object out both ways: Grasshopper's slider layout only
  ever grows for a longer name, so a slider kept a long name's width after `clear`, a
  shorter name, and ctrl-Z alike. The rename now sizes the slider to its name in both
  directions, keeps the right edge where it was when the control feeds a component to its
  right (a longer app-facing name used to walk straight over the input it was anchored
  to), and records name and layout as one undo step, so ctrl-Z restores both.
- **Session open** means any live terminal for the file. With two terminals open, closing
  the newer one used to flip the button back to Build while the older one still answered
  — and the click spawned a third. The session now tracks every terminal launched for it
  and demotes only when the last one closes; the log line says how many remain.
- A Save As keeps both files honest: the live Claude session follows the renamed
  document (its plate keeps reading Session open, the old path reads Build), the plate says
  where the app stayed, and the companion app resolves its definition by the file path its
  home is keyed to on every channel — state, geometry, and the live stream — so the page
  follows the file, not the terminal, and reads closed until that file is open again.
- The 401 page for a bare or stale app address is headed "Open this app from Rhino" and
  explains the run key, instead of telling someone who typed the plate's address that
  their link expired.
- The kit's panel textarea and text set `dir="auto"`, so right-to-left prose lays out
  against its own base direction.
- The shipped assemblies carry their identity: version 0.3.0, publisher, product, and
  copyright on the `.gha`, the `.rhp`, and both libraries (Grasshopper's missing-plugin
  dialog on a machine without Wireify used to show a blank version and no publisher), and
  the Grasshopper assembly reports its version and icon.
- The canvas capsules agree on casing: `wireify #1` on sockets and converted components,
  `wireify` on touched objects — lowercase everywhere, like the kit and the home folders.
- Failure on the app page names its actual cause and heals where healing is possible. The
  server distinguishes four states — live, definition closed, manifest missing or
  unparseable (a parse error names its line: `WIREIFY_APP_BAD_MANIFEST`), and an expired
  link — and the page switches on them: a closed definition reconnects by itself seconds
  after the file reopens (no refresh), a manifest fault says which file and returns the
  moment it parses, and an expired link says the link expired and how to get the fresh
  one. "Is Rhino still open?" is reserved for the one case where it is the honest
  question: the server is actually unreachable.
- The 3D viewport frames the model itself, not its bounding sphere: the camera distance
  is computed from the projected corners against both FOVs (a long thin truss used to
  land at a third of the canvas), the ground grid is sized and seated under the model
  instead of squatting at the world origin, and a stray click in the viewport no longer
  disarms the auto-refit that keeps the model framed through layout settling — only an
  actual camera move does. Meshes now draw their crease edges in the accent colour (a
  solids-only view used to have no contrast between fill and grid in either theme), fit
  frames the model's own vertices and re-centres on the projected silhouette (the bbox
  centre left one side of the frame slack under perspective), and the viewport reads its
  four colours from `--ify-viewport-*` tokens a theme can override.
- Readable errors where they were missing: a terminal that outlives a Rhino restart gets
  a plain-language message naming the rotated secret and the fix (instead of an HTML 404
  from the client's auth probing); `set_source` refuses an SDK-mode script whose
  `RunScript` signature does not match the component's params, naming both sides and the
  recipe, instead of letting the engine silently rewrite the signature; `set_typed_io`
  reports whether the param set actually changed; `set_source` with `solve: false`
  returns a schema-valid receipt.
- Long tool calls no longer run as MCP tasks. The scaffolded home config sets a ten-minute
  per-call timeout (`request_timeout_ms`) instead, and Claude Code moves calls past two
  minutes to the background on its own — same behavior for heavy solves, a much smaller
  dependency set inside the plugin.
- The server enforces the 2026-07-28 transport rules: POST-only on the MCP endpoint, `Origin`
  and `Host` validation on every surface (the companion app included), and protocol errors
  carried on real HTTP status codes.
- Scaffolded home settings use an `Edit` permission rule for the devlog — current Claude Code
  no longer matches the old `Write` rule and warned about it at startup.

## [0.2.0] - 2026-07-24

### Added

- Multi-definition sessions: with several `.gh` files open in one Rhino, each Connect is its
  own session bound to its definition — one shared local server, one terminal per file
  (window titled with the definition's name), each carrying that definition's memory. Tool
  calls route to the session's own document: reads work from a background tab; mutations
  require it to be the active canvas and refuse otherwise (`WIREIFY_DOC_NOT_ACTIVE`, with
  `WIREIFY_DOC_NOT_OPEN` when the file was closed). The socket button and terminal-closed
  handling are per-definition, and `get_document_summary` reports `isActiveCanvas`.
- `convert_staged` auto-selects type hints for un-hinted inputs from the live wired data:
  one mappable CLR type picks its concrete token (verified against the component's own hint
  registry), so script variables arrive as native types (`rg.Line`, `str`) instead of the
  un-hinted mode's script-doc Guid references. A mixed-type input tree — which no single hint
  can fit — stays un-hinted and puts an explicit warning in the result naming the two ways
  out (separate hinted inputs, or in-script Guid dereference via scriptcontext).
- An explicitly requested hint that doesn't exist fails loudly, naming the component's real
  hint tokens (`string`, `double`, `Line`, ... — Python names like `str` were never valid).
- Introspection reports each param's selected type hint (`hint`) and, on script params, the
  deduplicated `availableHints` list — `typeName` never reflects hints, which made hint
  changes look ignored.
- Live wiring on introspection: every param carries `sources` (what feeds it) and
  `recipients` (what consumes it), capped at 50 each with true totals — the mechanical
  answer to "is this component actually spare?" before reusing or rewiring anything.
- Upstream-clip detection is user-visible: a staged input carrying text of exactly 32767
  characters (the panel paste limit) puts an orange warning on the socket itself, and the
  same message rides the introspection payload's new `warnings` field.
- Wired-input introspection reports the goo wrapper class per type (`goo`, e.g. `GH_Line`)
  next to the unwrapped CLR type, and each sample's full `valueLength` — un-hinted script
  variables receive the goo, and a length of exactly 32767 exposes upstream panel clipping.
- `delete_component` tool: removes a Wireify socket or a script component (anything else is
  refused) as one undo step — wires included, so ctrl-Z restores everything.
- `set_panel_text` tool: writes text into an existing Panel component (one undo step) — with
  `wire` this completes the large-payload bypass tool-side (path into a panel, panel into a
  Read File, per-line list into the socket).
- `wire` accepts floating params (panels, sliders, file paths) on either end — a floating
  param is its own single param, addressed with index 0. Previously only
  component-to-component wiring worked.
- `run` and `read_runtime_errors` report native components and floating params, not just
  script components: a leaf param (a Line/Point container terminating a chain) reports
  itself as its own single output — previously an empty report with no way to read its
  live value. `runCount: -1` is documented as "no run counter on this object", not a failure.
- Runtime reports shaped like input reads: each output carries tree stats, a type histogram,
  and capped samples with true totals — tree preservation is verifiable from the report of
  the mutation that just ran, with zero extra calls.
- Drift guard: the in-file provenance header now carries a fingerprint of the code below it;
  `set_source` on a component hand-edited outside Wireify (the GH script editor) refuses
  with `WIREIFY_EXTERNAL_EDIT` and embeds the current code to merge — an engineer's manual
  edits can no longer be silently clobbered by a revise. `overwriteExternalEdits: true` is
  the deliberate-discard path, for use only with the user's explicit OK.
- Stable error codes: server refusals and failures carry `WIREIFY_*` codes with their
  recovery protocol in-band (`WIREIFY_BUSY`, `WIREIFY_QUEUE_TIMEOUT`, `WIREIFY_NOT_FOUND`,
  `WIREIFY_INPUT_WIRED`, `WIREIFY_EXTERNAL_EDIT`, `WIREIFY_DOC_NOT_ACTIVE`, ...);
  `WIREIFY_NOT_FOUND` embeds the live W-registry so a stale id self-repairs without
  re-orientation.
- Two-strikes leash, mechanical: from the second consecutive failed mutation on the same
  component, the error carries a `LEASH:` line telling the agent to stop and report the
  exact error instead of iterating blind.
- Managed lesson ledger: each home's `MEMORY.md` gets a Wireify-managed header (refreshed
  every Connect) with a dated-entry contract and an 8,000-character budget with a live
  usage line; Connect-time maintenance dedups and overflows old entries to
  `MEMORY-archive.md` with timestamped `.bak` snapshots taken first. Pre-0.2 free-text
  ledgers are left untouched with a visible "maintenance skipped" note.
- `wireify-retro` skill: user-invoked ledger consolidation — the plan is shown in chat
  before anything is touched; merge, rewrite, promote, archive — never delete.
- Shared tier under `~/.ify/wireify/`: `defaults.md` (standing conventions, imported into
  every session; seeded once, section-merged on updates, with a `## Promoted lessons`
  staging section) and a user-owned `skills/` directory copied into each home at Connect.
- Home identity that survives renames and moves: every Connect writes `.wireify/home.json`
  (definition path, content hash, last-Connect stamp). A Connect with no home adopts a
  renamed or moved definition's old home by COPY — the original stays untouched, stamped
  `adoptedInto` and `orphanedAtUtc`; file copies and SaveAs scaffold fresh homes. When an
  adoption cannot be resolved automatically, `.wireify/adoption-candidates.json` hands the
  candidates to the session for a user-confirmed recovery.
- Orphan sweep: homes whose `.gh` disappeared are stamped orphaned and age into `archive/`
  after 90 days — never deleted; a restored file clears the stamp.
- `~/.ify/wireify/homes.md`: a generated, human-readable index of every agent home (its
  `.gh` path, last Connect time, active/orphaned/adopted status, ledger size), regenerated
  on every Connect from the per-home identity records. A read-only snapshot — the records
  stay authoritative.
- Per-Connect memory glance: the connect steps (Rhino command line, panel, connect log)
  carry one line per home — lesson count, newest lesson date, ledger usage
  (`memory: 12 lessons (last 2026-07-08), 5,132/8,000 chars`) — so the compounding memory
  is visible where you already look.
- Build identity everywhere: the panel/console listening line, the MCP serverInfo, and
  `get_runtime_info`'s `wireifyBuild` all carry the compile-baked version + build stamp
  (`0.2.0 build 2026-07-10 04:26`) — after a swap, ten seconds tell you which build actually
  loaded, and a re-unblocked stale DLL can no longer masquerade as fresh.
- Connect steps echo to the Rhino command line — refusals and failures included, not just
  the panel and the connect log.
- Session log on disk: every tool call lands in `~/.ify/wireify/logs/session-*.log` — an
  entry line as the call starts (a call that never returns still leaves its name) and the
  outcome with timing, plus the real exception and stack on failure — field issues stay
  diagnosable after Rhino closes.
- Dual-install detection, two layers: a second Wireify install is called out at startup
  with the stale path (instead of a bare "ID already in use"), and a second loaded copy of
  the plugin's assemblies is warned about at server start — in that state tool errors lose
  their detail, and the warning says so.
- `get_document_summary` bounded for production canvases: the components list caps at
  `maxComponents` (default 300; selected and Wireify-managed objects kept first) with
  `componentsTruncated` + `totalObjectCount` reporting the cut, and `nameFilter` for
  targeted lookups — the Wireify registry itself is never truncated.
- `introspect_component` / `introspect_selected` now handle floating params (panels,
  sliders), reporting them as their own single output instead of refusing.
- Dev mode, an opt-in feedback logger for people testing Wireify: saying `dev mode on` in a
  session makes it append structured findings (bugs, friction, successes, ideas — with build
  stamp, verbatim prompts and error text) to a local `~/.ify/wireify/devlog.md`;
  `dev mode wrap` closes the session with a short summary. Off by default, local-only, and
  listed in the README Trust section like everything else Wireify persists.

### Changed

- `wire` refuses an occupied input by default (`WIREIFY_INPUT_WIRED`, document untouched):
  merging branches is an explicit `mode: add`, swapping wires an explicit `mode: replace` —
  0.1.0 merged silently, which contaminated live inputs. Every wire now pushes an undo
  record (plain wires had none), a replace restores in one ctrl-Z, and the `WireResult`
  receipt echoes what connected where and what was replaced.
- `wire` solves after wiring (parity with interactive wiring), so a read right after it
  sees live data instead of an empty pre-solve preview.
- `convert_staged` drops staged inputs that have no wires at conversion time unless they
  are declared explicitly in `inputs` — the socket's spare default input no longer survives
  as a permanently dead param on the built component. Every drop is named in the result's
  warnings; coverage is required for wired inputs only.
- `WIREIFY_DOC_NOT_ACTIVE` now says plainly that reads keep working from a background tab —
  the agent keeps investigating and asks for the front tab only when it is ready to mutate.

### Fixed

- Tool errors name the real failure (`set_source failed — TimeoutException: ...`, carried
  behind the MCP SDK's standard "An error occurred invoking 'x': " prefix) instead of the
  bare generic mask — reflection and task wrappers are unwrapped and forwarded, and a
  loopback test now pins the end-to-end shape so the bare mask can never return unnoticed.
- One filesystem hiccup (a sync tool or scanner holding the log file) no longer silently
  kills the session log for the rest of the Rhino run: the writer suspends for 30-second
  retry windows, says so once on the panel and console, and reports when it resumes.
- Report and sample strings are sanitized for transport (lone surrogates, stray control
  characters — including a surrogate pair split by the sample cap) so canvas data can never
  fail response serialization, which runs outside the tool error handling and would mask
  the whole result.
- `run` expires and recomputes in one solve, so its report reads data the solve finished
  writing — never a param caught cleared between two solves.
- A script rebuild that exceeds its timeout now fails honestly instead of silently
  continuing with a stale compile; a faulted rebuild reports its actual exception.
- Runtime reports cap output values (first 25 per output, each value capped at 8k — roomy
  enough for a schema-probe dump in one value — true total reported) and input samples cap
  their value text — a heavy wire can no longer flood responses or pin the UI thread
  stringifying data nobody reads.
- `set_io`'s returned introspection now reports the hints just declared even where the raw
  registry read comes back empty — the echo is truthful about what was applied.
- With two definitions open in one Rhino, a session could silently read — and mutate — the
  file whose tab happened to be in front instead of the one it was connected to, and a
  fresh file's socket showed `do #1` off another file's live terminal. Sessions are now
  document-bound (see the multi-definition entry under Added), and lessons always record
  to the ledger of the definition they came from.
- Scaffolder writes are atomic (temp + replace) — a torn write can no longer truncate a
  home's `CLAUDE.md` or half-write its config.

## [0.1.0] - 2026-07-04

First public release, on the Rhino Package Manager (Windows). Live-validated on
Rhino 8.29 (Windows):

- In-process MCP server inside Grasshopper (official C# SDK, streamable HTTP, stateless)
  on `127.0.0.1:9473+`, loopback-only, per-session secret.
- The numbered Wireify socket: stage and name inputs on the canvas, then
  `do #1: <task>` in the connected Claude Code terminal.
- `convert_staged`: the socket becomes a stock Python 3 script component in place —
  explicit parameter construction, wires migrated, `W<n>` nickname kept, one undo step.
  Saved definitions carry no Wireify dependency.
- 14-tool surface including live wired-input reading (`read_input_data`), explicit I/O
  definition (`set_io`), source reading (`get_source`), and runtime discovery.
- One-click Connect (socket button, `_Wireify` command, or the panel): scaffolds a
  per-definition agent home with Grasshopper skills and compounding memory, merges the
  MCP config, pre-trusts the generated home, and opens a terminal already running Claude.
- Rhino panel with live connect status and a scope-tagged log; number badge overlay and
  an in-code provenance header on converted components.
