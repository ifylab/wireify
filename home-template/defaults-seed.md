# Wireify defaults (shared)

Standing preferences applied across every Wireify definition. **Edit this to your standards — it is yours.** Per-definition lessons live in each home's `MEMORY.md`; this file holds the conventions that do not change between files. Seeded with sensible defaults; the `EDIT` markers are where your firm's specifics go.

This file is imported into context in every session for every definition — keep it lean (a few KB of rules, not documentation). Its procedural sibling is the `skills/` folder beside it: any skill folder you keep in `~/.ify/wireify/skills/` lands in every home's `.claude/skills/` at Connect (Wireify's bundled skills win name collisions). New seed sections from plugin updates append at the end; your edits are never touched.

## Units and tolerance

- Units: use the Grasshopper document's units. <!-- EDIT: your default system + analysis units, e.g. geometry in mm; structural results in kN / kNm / MPa -->
- Tolerance: use the document's absolute and angle tolerance for geometric comparisons; do not hard-code epsilons.

## Runtime and libraries

- Default runtime: CPython 3 (Rhino 8). IronPython 2 only when the target requires it.
- Go-to: `rhinoscriptsyntax` (rs), `Rhino.Geometry` (rg), `ghpythonlib`. Pip packages via the `# r:` header (e.g. `# r: numpy`).

## Code style

- Clear names; validate at input and geometry boundaries (nulls, empty branches, units); fail loud with Grasshopper runtime messages.
- Preserve data trees; restructure only deliberately and visibly.

## AEC / structural conventions

The `aec-structural` skill reads these. <!-- EDIT each line to your firm's standards. -->

- Sign convention (tension +/-, sagging vs hogging moment, gravity axis): <!-- EDIT -->
- Coordinate frame (local member vs global; moment right-hand rule): <!-- EDIT -->
- Section / material source (library, naming, lookup): <!-- EDIT -->
- Load cases / combinations convention: <!-- EDIT -->
- Rounding / reporting: <!-- EDIT -->

## Promoted lessons (agent-appended — review, then fold into your sections above or delete)

Lessons promoted from per-definition ledgers land here as one-liners suffixed `(from <home>, date)`. Fold the keepers into your sections above; delete the rest. Nothing here is authoritative until you move it up.
