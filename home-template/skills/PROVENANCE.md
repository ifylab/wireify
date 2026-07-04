# Provenance — Wireify home skills

The Grasshopper knowledge skills shipped in each Wireify project home are **composed by [skillmeld](https://github.com/ifylab/skillmeld)** from existing community skills, then layered with one authored Wireify skill. This is the `.ify` design at work: skillmeld discovers and merges the community knowledge; Wireify gives it a live body.

## Vendored, composed by skillmeld (unedited)

Composed 2026-06-28 (skillmeld Track B, all-MIT); every instruction traces byte-for-byte to a source below. Vendored here 2026-06-29, pristine.

- **`rhino-grasshopper-dev`** (MIT) — from https://github.com/jianhuichou/rhino-grasshopper-dev-skill
- **`scripting-reference`** (MIT) — from https://github.com/Amanbh997/Claude-skills-for-Computational-Designers

Both are MIT and retain their MIT terms; MIT is compatible with this project's Apache-2.0 license. The carried `references/` and `scripts/` ship with `rhino-grasshopper-dev`.

## Authored for Wireify

- **`wireify-loop`** (Apache-2.0) — the live introspect -> typed-Python -> run -> fix loop driven by the Wireify MCP tools, the CPython-3 / IronPython-2 correctness rules, the `# r:` and typed-I/O conventions, and the per-`.gh` memory discipline. It is the entry point for a Wireify home and routes deeper questions to the two vendored skills.

## Wireify adaptations

- skillmeld's generated `orchestrator` (a generic dev-vs-reference router) is **not** shipped — `wireify-loop` takes the entry/routing role in the Wireify context.
- The vendored skills are kept **unedited**. Where they describe live automation through the community RhinoMCP server / `run_python`, that does **not** apply inside Wireify — `wireify-loop` directs live automation through the Wireify MCP tools instead. Their surplus scope (C#/Yak/Revit/Three.js) is fine as reference; `wireify-loop` keeps the live loop focused on Grasshopper Python.
