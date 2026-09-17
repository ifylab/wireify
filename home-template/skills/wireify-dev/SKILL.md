---
name: wireify-dev
description: "Wireify dev mode -- opt-in feedback logging for people testing Wireify itself. Use ONLY when the user explicitly turns it on or drives it: 'dev mode on', 'dev mode off', 'dev mode wrap', or a 'log: ...' note while dev mode is on. Never load it for normal Grasshopper work; it is inert unless asked for. While on, the session silently appends structured findings (bugs, friction, successes, ideas, data-shape surprises) to the local ~/.ify/wireify/devlog.md that the tester later hands to the Wireify developers."
---

# Wireify dev mode — the tester's devlog

Dev mode turns a normal session into a recorded test session for the people building Wireify. While it is on, you keep doing the user's Grasshopper work exactly as always -- and additionally append short, structured entries to ONE local file, `~/.ify/wireify/devlog.md`, across all sessions and definitions. Local-only: the file never leaves the machine; the tester hands it over themselves. Everything here is additive -- dev mode never changes how you handle the actual task, and the `MEMORY.md` lesson discipline continues unchanged.

## Turn on ("dev mode", "dev mode on")

1. **Open the ledger.** Read the tail of `~/.ify/wireify/devlog.md` (last ~60 lines). If the file does not exist, create it with exactly this header, asking the user's name once:

   ```
   # Wireify devlog — tester feedback (format 1)

   Structured, local-only findings appended by dev-mode sessions while testing Wireify.
   Hand this file to the Wireify developers when asked; nothing is sent anywhere by itself.
   Entry types: BUG (defect) / FRICTION (worked, but fought) / SUCCESS (worked well) /
   IDEA (feature ask) / SCHEMA (data-shape surprise) / NOTE (tester's verbatim remark).
   Severity, on BUG only: blocker / major / minor.

   - Tester default: <name>
   ```

2. **Number the session.** Next `S<n>` after the last `## Session S<n>` header (S1 for a fresh file).
3. **Stamp it.** Call `get_runtime_info` once and append the session header:

   ```
   ## Session S3 — 2026-07-17 09:14 — tester: <your name>
   - Build: 0.2.0 build 2026-07-10 20:07 | Rhino 8.19 | model: <your model>
   - Home: facade-panels-3f2a9c1d (facade-panels.gh)
   - Session log: session-20260717-091412.log
   ```

   `Build`/`Rhino` come from `wireifyBuild`/`rhinoVersion`; the session log is the newest `session-*.log` under `~/.ify/wireify/logs/` (omit the line if none found). If the user names a focus or checklist at activation, record it verbatim as checkboxes under a `### S3 focus` line.
4. **Confirm in one line** -- "Dev mode on — logging to ~/.ify/wireify/devlog.md" -- and from then on stay silent about logging: no per-entry announcements, no logging talk in normal replies.

## What to log, while on

Append an entry when any of these happens -- after finishing the user-facing work, never instead of it:

- **Immediately on a surprise:** any `WIREIFY_*` error, a LEASH line, a wrong or unexpected result, the user correcting you or fighting the tool.
- **After each completed user task:** one SUCCESS (worked well) or FRICTION (worked, but fought) entry.
- **On `log: <text>`** (or "add to the devlog: ..."): a NOTE entry carrying the user's words verbatim.
- **SCHEMA** when a data-tree shape drove a decision or a failure: paths, branch counts, types, one line.

Entry shape -- at most ~15 lines, only the lines that apply:

```
### S3.2 BUG — convert_staged flattened the tree
- Severity: major
- Doc: facade-panels.gh            (only when it differs from the session header)
- Prompt: "do #4: convert this keeping the tree structure"
- Did: introspect_selected -> read_input_data -> convert_staged
- Expected: paths {0;0}..{0;5} preserved
- Actual: single branch; retry raised WIREIFY_EXTERNAL_EDIT (error text verbatim)
- Evidence: session-20260717-091412.log around 09:31
- Repro: once, not reproduced / exact steps if it repeated
- Outcome: done via set_typed_io workaround, ~9 exchanges
- Watch: focus item 5             (only when a focus list exists)
```

Prompts and error text go in **verbatim** -- never paraphrased. Name files and components; never copy file contents, code bodies, or anything that looks like a secret. A repeat of an earlier finding gets one short entry -- `### S3.4 BUG — repeat of S1.3 (same refusal on a fresh file)` -- not a duplicate.

**Appends only.** The activation read gives you the tail anchor; add every entry with Edit after the file's last line. Never rewrite the file and never Write over it -- history above the tail is other sessions' testimony.

## Wrap ("dev mode wrap", "dev mode off", or the user is clearly closing out)

Append the wrap, tick any focus items this session covered, confirm in one line with the file path, and -- on "off" -- stop logging for the rest of the session:

```
### S3 wrap
- Tasks: 6 attempted / 5 done / 1 abandoned
- Top pains: 1) ...  2) ...
- Focus covered: items 1, 5
- Keep: ...   Change: ...
```

A session that ends without a wrap is fine -- the next session's header closes it implicitly.

## Tester method (the residue of eight PC rounds)

Lessons every test session rediscovered the hard way. Follow them while dev mode is on; log a NOTE when one saves the round.

- **Prove the build first.** The listening line and the fixture's meta line (`fixture v<n> · server <version> build <stamp>`) must both carry the build under test before any item runs -- one round tested a stale page, another a stale build, and every finding was noise.
- **Snapshot at the start, re-verify at the end.** Record the control values and a geometry fingerprint (a hash over `api/geometry`'s payload is cheaper and stronger than per-value comparison) when the session opens; re-read them before closing. `get_document_summary`'s `unsavedChanges` / `savedAt` say whether a changed object count is drift or an unsaved document -- check them before writing up a "drift" finding.
- **File size is not canvas state.** The same content saved three times at three sizes in one round; compare mtime and content, never size alone.
- **Screenshot before you consume.** A placement or layout observation needs the canvas as it was BEFORE test objects are deleted -- two rounds lost the evidence by cleaning up first. `introspect_component`'s `bounds` and the params' `gripY` make placement checkable numerically as well.
- **Verify before a Build that rotates the token.** A Build-triggered restart ends the previous working session's MCP client; anything to verify through that session must be verified first.
- **Probe the app API through `node`, not the browser**, for values, frames, and geometry (KIT.md "Probing without a browser"); count SSE frames there -- an in-page counter dies with any reload. Reserve Chrome for the rendered page, and expect its harness quirks (KIT.md lists them: rotating tab ids, stale framebuffers in background tabs, dropped non-string returns, `[BLOCKED: …]` false positives).
- **Two frames on a grip click are a real solve.** Grasshopper expires a slider on mouse-up even at an identical value; do not log that as chatter.
- **Never test "did the geometry update?" with a slider that does not feed the geometry** -- read the slider's `recipients` first and build an island probe if it has none on the chain under test.
- **Change the observation channel before you file.** Two round-8 findings with four consistent reproductions each were the harness, not the product (a hidden tab single-stepping a damped camera one screenshot per frame; layout numbers read before a repaint). Reproducibility is not the discriminator -- a second channel is: a tool's numbers against a human's eyes, pixels against a frame counter, node against Chrome.
- **An undo test is valid only when the action under test is the top of the undo stack.** A ctrl-Z that "failed" was undoing three later renames queued behind it. Check what is on top before you press.
- **Never run a subagent against a canvas the parent is also mutating.** It reports in good faith on a manifest you were editing underneath it. Fence it to its own objects, or stop editing while it runs.
- **"X happens on its own when the user does Y" needs no timing.** The harness has no backgroundable listener, so a wait blocks the conversation and the tester reads the instruction before or after it. Leave the page open, have the tester make the change and stay, then screenshot without reloading.
- **The Chrome extension can disconnect outright** ("Browser extension is not connected"). Re-reading the tab list does not recover it, unlike a stale tab id: reconnect the extension, then re-read the tabs.
- **Measure, never glance.** A tester's eye read a post-undo slider as "narrower" when it measured unchanged, and a "13 px per cycle" drift model came from a window with keystroke ambiguity; the true mechanism (an overshoot proportional to name length) showed only in the cleanest single-step measurement. Quote that one.
- **Grasshopper's profiler bar reads as an error.** A red timing readout pinned under a component is a cost colour, not a fault badge; `read_runtime_errors` returning `messages: []` settles it in one call.
- **`javascript_tool` blanks a whole result that contains a GUID** (`[BLOCKED: Cookie/query string data]`). Return booleans and counts from page scripts, never ids.
- **Cookies are per ORIGIN, shared across tabs.** Pressing Open app re-mints the session cookie for every tab of the app on that origin, so a stale tab goes straight from disconnected to live; to observe the expired-link copy, relaunch Rhino and do NOT press Open app.
- **A Save As splits the definition two ways.** The home and the app follow the PATH (the original file); the terminal follows the INSTANCE (the copy). Verify app behaviour with both files open, and read `docName` off every channel (state, geometry, stream) before believing any one of them.
- **`javascript_tool` blanks plain sentences too.** The product's own refusal copy — no token, URL or GUID in it — came back as `[BLOCKED: …]`. Reconstruct from `length`, `wordCount`, `firstWord` and a battery of `indexOf` booleans; that is better evidence than the raw string anyway.
- **An observer on `kit/fixture.html` dies at the heal.** The fixture reloads itself by design when the definition is back; arm the MutationObserver on the kit page for a sequence that crosses a heal, and use the fixture only for single states.
- **A control created with `nearId` + `nearInput` is placed, not wired.** Wire it (or ask the agent to) before testing anything that depends on a wire — and know that placement-dependent rules (the rename's anchored edge) hold with or without one since round 12.
- **Read Grasshopper's Edit menu label before every undo or redo keystroke** (`Undo wireify convert`, `Redo wireify app push`). It names what the next ctrl-Z will undo; a keystroke that never registered, or three undos where one was meant, both show there first. An undo test whose action restores a state identical to its starting point cannot distinguish "no undo" from "three undos" — re-stage it with a visible delta.
- **Count the open Grasshopper tabs before trusting any per-definition observation.** A Save As, or simply two documents open, splits "the definition" into a path-keyed half (the home, the app) and an instance-keyed half (the session). One round filed and withdrew a finding, and mis-read a real one, before the tester's eyes found a second canvas tab.
- **Print the whole object before concluding a field is empty.** Control payloads are typed per kind (`value` for sliders, `text` for panels, `items`, `colour`, `axes`); a per-view `warnings` sits on the view object, not on the frame. A null in the field you happened to read is usually the wrong field, not an empty product.
- **When a control "stops working", measure the layout before the code.** A slider went dead because the label column had taken the range input's width (0 px) once help lines arrived; `getComputedStyle(grid).gridTemplateColumns` and the input's `getBoundingClientRect()` settle it in one probe, and a state push through the API proves the data layer is fine.
- **`innerText` does not contain a textarea's value.** Read `.value` — a page rendering Persian perfectly reported "not in DOM" to a probe that searched `innerText`.
- **A backgrounded tab throttles timers; arm a `MutationObserver`.** A `setTimeout(4000)` in a background tab is not four seconds of wall clock, and a single sample cannot tell "never" from "not yet" — one round was a message away from filing "the page never heals". Observe DOM changes; never record a latency measured from a driven background tab.
- **A portability claim is only testable with the plugin gone.** Every proxy (stock component type, no Wireify imports, a clean `.ghx` byte scan) predicts it; only moving the plugin folder out of `Libraries` and opening the file observes it — which is also the only way the missing-plugin dialog's metadata gets seen.

## Hard rules

- **The work comes first.** Dev mode never alters how you handle the user's actual task -- same tools, same care, same pace. Log after, not during.
- **Silent while on.** One line at activation, one at wrap; between them the devlog is never mentioned unless the user asks.
- **Local-only.** Never send, upload, or copy the devlog anywhere; if asked to share it, tell the user where it is and let them hand it over.
- **Not memory.** The devlog is testimony for the Wireify developers, not a lesson ledger -- keep writing real lessons to `MEMORY.md` exactly as the home's rules say, and never import the devlog into context wholesale (the tail read at activation is enough).
- Off is off: without an explicit activation this skill does nothing, and a session where it was never activated writes nothing.
