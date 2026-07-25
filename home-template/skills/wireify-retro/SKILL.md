---
name: wireify-retro
description: "Consolidate this Wireify definition's lesson ledger. Use when the user asks to clean up memory, consolidate lessons, tidy the ledger, or asks what keeps going wrong with this definition — or when CLAUDE.md carries the ledger-over-budget directive. Reads MEMORY.md and MEMORY-archive.md, presents a consolidation plan, applies only approved changes, and records the pass in the ledger itself. One pass per invocation; never destroys content."
---

# Wireify retro — consolidate the ledger

`MEMORY.md` at this home's root is the definition's lesson ledger, imported into context every session. Left alone it only grows: near-duplicate lessons pile up, entries go stale against the current canvas, and cross-file rules sit here instead of `~/.ify/wireify/defaults.md`. This skill is the guided consolidation pass -- strictly user-invoked, one pass per invocation.

The mechanical side is already handled elsewhere: Connect dedups byte-identical entries and archives overflow to `MEMORY-archive.md` (snapshot-first). This pass is the SEMANTIC side -- merging, rewriting, and promoting -- which only happens with the user in the loop.

## Procedure

1. **Read the state.** `MEMORY.md`, `MEMORY-archive.md` (if present), and the usage ledger JSON under the home (if present -- ships in a later version; skip silently when absent). Note the header's usage line (`Ledger: N / budget chars`) -- landing under budget is the goal, not the trigger; run whenever asked.
2. **Group.** Cluster entries by W-number and by topic (same symptom family, same API, same data shape). While grouping, answer the diagnostic question if the user asked it: "what keeps going wrong here" = the largest clusters, stated plainly.
3. **Present the plan in chat, before touching anything.** For each proposed change, one line with the outcome:
   - **Merge**: near-duplicate entries -> ONE stronger entry (newest date, sharpest Symptom/Cause/Fix/Applies-when; note "merged from N entries" in the title line's tail if useful).
   - **Rewrite**: an entry stale against the current canvas -- verify with ONE cheap read-only probe (`get_document_summary` / `introspect_component`) where that settles it; never rewrite on assumption.
   - **Promote**: a lesson that would hold in a brand-new `.gh` -> one line appended to `## Promoted lessons` in `~/.ify/wireify/defaults.md`, suffixed `(from <home folder name>, YYYY-MM-DD)`; keep only the definition-specific residue here. (That write sits outside the home -- Claude Code may confirm it once; the prompt is the review gate.)
   - **Archive**: an entry that is genuinely obsolete (its component gone, its API path removed) -> move it to `MEMORY-archive.md` verbatim. Archiving is the strongest action available -- there is no delete.
4. **Apply ONLY what the user approves**, with Edit, keeping the file's contract intact: the managed header block untouched, entries at `### YYYY-MM-DD [W<n>] title` level under `## Lessons`, newest first, Symptom/Cause/Fix/Applies-when lines.
5. **Record the pass**: append `### YYYY-MM-DD [-] retro — consolidated N entries (M merged, K archived, J promoted)` as the newest entry, then read the file's tail back once to confirm the write landed.

## Hard rules

- **Never remove content that is not preserved somewhere** -- a merge keeps the surviving entry's substance; anything dropped whole goes to `MEMORY-archive.md` first. There is no destructive path through this skill.
- **Never touch an entry you cannot parse** (free text from before the ledger format, foreign sections, the user's own notes). Say it was skipped and why.
- **One consolidation pass per invocation.** Present, apply approved changes, record, stop -- no second sweep without being asked again.
- Do not invent lessons, backfill dates, or reword the user's own phrasing beyond what a merge requires.
