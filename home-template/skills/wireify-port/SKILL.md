---
name: wireify-port
description: "Migrate a legacy IronPython 2 GhPython component to a typed Rhino 8 CPython 3 component, equivalence-checked. Use when modernising an existing IronPython 2 / GhPython script to CPython 3, when a component is red from mixed Python-2 and Python-3 idioms, or when porting a Rhino 7 definition to Rhino 8. Runs old and new in parallel on the same live inputs and diffs outputs until equivalent."
---

# Wireify port — IronPython 2 to CPython 3

The documented Grasshopper pain: AI rewrites a legacy IronPython 2 script as Python 3 (or mixes the two) and the component goes red. This skill ports deliberately and **proves equivalence** before replacing anything. It runs on top of `wireify-loop` (the live loop) and uses `scripting-reference` for exact API signatures.

## When to use

- An existing IronPython 2 / GhPython component (Rhino 7, or legacy GhPython on Rhino 8) should become a Rhino 8 CPython 3 component.
- A component is failing because Python-2 and Python-3 idioms are mixed.
- A whole Rhino 7 definition is moving to Rhino 8.

## The flow (a Task — old and new in parallel, diff to equivalence)

1. **Read the original.** `introspect_selected` on the legacy component; **`get_source`** for its IronPython 2 code; `read_input_data` on its live inputs (read-only calls — batch them in one message) so you know the real types, tree shape, and samples (not what the code assumes).
2. **Plan the port.** List the Py2 -> Py3 idiom changes this specific code needs (checklist below) and the I/O the live data implies (same names as the original, access sized to the data).
3. **Regenerate, typed, on CPython 3.** `create_python_component` `runtime: cpython3`; **`set_io`** with the original's input/output names and data-sized access (plain script-mode derives nothing from source); `set_source` with the ported code (`#! python 3` is auto-prepended; add `# r:` lines for any pip deps the new code needs).
4. **Wire both in parallel.** Feed the **same** upstream outputs into both the old component and the new one.
5. **Run both.** `set_source` already returned the new component's post-solve report (messages + outputs); `run` the legacy one — its result carries the report too. `read_runtime_errors` only re-reads without solving.
6. **Diff outputs.** Compare data-tree structure and values **within document tolerance** (geometry is equivalent if within tolerance, not bitwise). If they differ, diagnose -- almost always a Py2/Py3 idiom (below) or a RhinoCommon/rhinoscriptsyntax signature drift between runtimes (check `scripting-reference` + `get_runtime_info`) -- fix the new component, re-run, re-diff. Loop until equivalent.
7. **Cut over (with approval).** Replacing or deleting the released old component changes the public surface (its GUID and wiring). **Stop and get explicit approval** before deleting/replacing it. Then record the port + any gotcha in `MEMORY.md`.

## Py2 -> Py3 idiom checklist (the usual breakages)

- **Integer division:** Py2 `/` floors for ints; Py3 `/` is true division. Use `//` where floor was intended; otherwise expect different numbers.
- **print:** Py2 statement `print x` -> Py3 function `print(x)`.
- **except:** `except E, e:` -> `except E as e:`.
- **range/xrange:** `xrange` -> `range`; Py3 `range`/`map`/`filter`/`zip` are lazy iterators -- wrap in `list(...)` if you index or reuse them.
- **dict:** `d.has_key(k)` -> `k in d`; `d.iteritems()`/`iterkeys()` -> `d.items()`/`keys()`; rely on insertion order only on Py3.
- **strings/unicode:** Py2 `str` vs `unicode` collapses to Py3 `str`; watch encoding at file/COM boundaries.
- **f-strings:** Py3 only -- fine in the new component, never in the old one.
- **imports:** prefer explicit absolute imports; some module names moved.
- **API drift:** the same RhinoCommon / rhinoscriptsyntax call can differ between the runtimes -- confirm signatures via `scripting-reference`, don't assume the IPy2 call is identical on CPython 3.

## Equivalence discipline

- Compare **tree structure** (path count, branch shape) and **values within tolerance**, per output. A passing diff is the only basis for claiming equivalence -- never claim it from reading the code.
- If outputs are geometry, compare within the document's absolute/angle tolerance, not exact equality.
- Keep both components on the canvas until the diff passes; only then propose the cutover.

## Boundaries

- Defer exact API how-to to `scripting-reference`; dev-workflow/packaging and GUID-preservation discipline to `rhino-grasshopper-dev`; the live tool loop to `wireify-loop`.
- Never delete, replace, or rewire a released component without explicit approval.
