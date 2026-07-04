---
name: aec-structural
description: "Type and generate Grasshopper Python for structural / AEC engineering semantics -- loads, moments, stresses, displacements, section and material properties, load cases and combinations, and member/node/element topology -- with strict units, tolerance, sign-convention, and coordinate-system discipline. Use for structural or AEC computational work where the numbers mean engineering quantities, not just geometry. Opt-in starter; deepen with the firm's standards in defaults.md."
---

# AEC / structural typing (starter)

A force is not just a `Number`. In structural and AEC work the data on the wires carries engineering meaning and units, and getting those wrong is silent and dangerous. This skill makes the loop type to **engineering semantics**, not just CLR types. It sits on top of `wireify-loop`; defer geometry API to `scripting-reference`.

This is a **starter** -- it encodes the general discipline. The firm-specific depth (Walter P Moore standards, section/material libraries, analysis-tool conventions, preferred sign conventions) belongs in the shared `defaults.md` and grows there over time. Where this skill says "confirm", confirm against `defaults.md` first, then the user.

## Type to the engineering meaning

When `read_input_data` reports plain numbers, ask **what they are** before typing:

- A force/axial (kN), shear (kN), moment (kNm), stress (MPa), displacement (mm), rotation (rad), area (mm2), inertia (mm4)?
- Carry the meaning and unit into the parameter name/nickname (e.g. `N_kN`, `M_kNm`, `L_mm`, `fy_MPa`) so downstream wiring is self-documenting.
- If the unit is ambiguous, validate or convert at the boundary -- never let an unlabelled number flow into a formula.

## Units and tolerance (the discipline that prevents silent error)

- Confirm the **document units** (`get_runtime_info` / the doc) and the **data's units**; they are not always the same.
- Convert **explicitly at boundaries**; never mix unit systems silently. Make every conversion visible in code.
- Use the model's **absolute and angle tolerance** for any geometric comparison; do not hard-code epsilons.
- Default unit system and conventions: read `defaults.md` (seeded, firm-editable). If it is silent, confirm with the user rather than assuming SI vs imperial.

## Sign conventions and coordinate systems

- State the **sign convention** in use (tension positive or negative? sagging vs hogging moment? gravity down as -Z?) and keep it consistent across inputs and outputs.
- Distinguish **local vs global axes** (member local axes vs model global; right-hand rule for moments). Convert deliberately; label which frame each quantity is in.
- Don't infer conventions from sample signs -- confirm them (`defaults.md`, then the user). A flipped sign is a guaranteed wrong answer that still "runs".

## Common AEC data shapes (map trees to structure)

- Per-member lists (member id -> value), per-node, per-element.
- **Load cases / combinations as tree branches** (one branch per case or combo) -- preserve that topology; do not flatten it away.
- Section libraries and material tables (lookups by name/id).
- Keep input/output paths aligned to the engineering structure they represent.

## Validation at engineering boundaries

Validate before computing: nulls and zero-length members, degenerate or missing sections, missing material properties, NaN/Inf analysis results, unit mismatches, out-of-range values (e.g. negative area), and inconsistent case/combination counts. Surface failures as Grasshopper runtime messages -- never return a quietly wrong number.

## Boundaries

- Geometry/API how-to -> `scripting-reference`. Dev workflow/packaging -> `rhino-grasshopper-dev`. The live loop and runtime rules -> `wireify-loop`.
- This skill is opt-in for engineering work; for purely geometric tasks the base loop is enough.
