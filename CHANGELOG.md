# Changelog

All notable changes to Wireify are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer.

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
