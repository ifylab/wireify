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

## Hard rules

- **The work comes first.** Dev mode never alters how you handle the user's actual task -- same tools, same care, same pace. Log after, not during.
- **Silent while on.** One line at activation, one at wrap; between them the devlog is never mentioned unless the user asks.
- **Local-only.** Never send, upload, or copy the devlog anywhere; if asked to share it, tell the user where it is and let them hand it over.
- **Not memory.** The devlog is testimony for the Wireify developers, not a lesson ledger -- keep writing real lessons to `MEMORY.md` exactly as the home's rules say, and never import the devlog into context wholesale (the tail read at activation is enough).
- Off is off: without an explicit activation this skill does nothing, and a session where it was never activated writes nothing.
