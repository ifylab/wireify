# Provenance — Wireify home skills

The Grasshopper knowledge skills shipped in each Wireify project home are **composed by [skillmeld](https://github.com/ifylab/skillmeld)** from existing community skills, then layered with the authored Wireify skills. This is the `.ify` design at work: skillmeld discovers and merges the community knowledge; Wireify gives it a live body.

## Vendored, composed by skillmeld

Composed 2026-06-28 (skillmeld Track B, all-MIT); every instruction traces byte-for-byte to a source below. Vendored here 2026-06-29; apart from the two marked local notes below, unedited.

- **`rhino-grasshopper-dev`** (MIT) — from https://github.com/jianhuichou/rhino-grasshopper-dev-skill
- **`scripting-reference`** (MIT) — from https://github.com/Amanbh997/Claude-skills-for-Computational-Designers

Both are MIT and retain their MIT terms; MIT is compatible with this project's Apache-2.0 license. The carried `references/` and `scripts/` ship with `rhino-grasshopper-dev`.

### Local modifications (2026-07-24)

Two in-file notes, both marked "*Wireify note:*" where they sit; nothing else in either vendored skill is altered:

- **`rhino-grasshopper-dev/SKILL.md`** — one note under the title: the RhinoMCP server / `run_python` tools it describes do not exist in a Wireify home (live automation goes through the `wireify` tools). A reader opening the file directly now sees the override that previously lived only in `wireify-loop` and here.
- **`scripting-reference/SKILL.md`** — the closing sentence pointed at a `references/` subdirectory that is not vendored with this skill; removed, with a note in its place.

## Authored for Wireify

All Apache-2.0:

- **`wireify-loop`** — the live introspect -> typed-Python -> run -> fix loop driven by the Wireify MCP tools, the CPython-3 / IronPython-2 correctness rules, the `# r:` and typed-I/O conventions, and the per-`.gh` memory discipline. It is the entry point for a Wireify home and routes deeper questions to the two vendored skills.
- **`wireify-port`** — the IronPython 2 -> CPython 3 migration flow (read the legacy code, introspect live inputs, regenerate typed, run both, diff until equivalent).
- **`wireify-retro`** — the user-invoked consolidation pass over the definition's `MEMORY.md` lesson ledger (merge near-duplicates, rewrite stale entries, promote cross-file rules, archive — never delete).
- **`aec-structural`** — opt-in structural/AEC engineering semantics for definitions whose numbers are engineering quantities.
- **`wireify-dev`** — opt-in dev mode for people testing Wireify itself: activated per session ("dev mode on"), it appends structured findings (bugs, friction, successes, ideas) to the local `~/.ify/wireify/devlog.md` for hand-back to the Wireify developers. Off by default; writes nothing unless activated.

## Wireify adaptations

- skillmeld's generated `orchestrator` (a generic dev-vs-reference router) is **not** shipped — `wireify-loop` takes the entry/routing role in the Wireify context.
- The vendored skills are kept unedited apart from the two marked notes above. Where they describe live automation through the community RhinoMCP server / `run_python`, that does **not** apply inside Wireify — `wireify-loop` directs live automation through the Wireify MCP tools instead (the in-file note now says so too). Their surplus scope (C#/Yak/Revit/Three.js) is fine as reference; `wireify-loop` keeps the live loop focused on Grasshopper Python.
