---
name: wireify-loop
description: "Entry skill for a Wireify project home. Drive the live Grasshopper Python loop with the Wireify MCP tools: resolve the user's numbered Wireify sockets (do #3: ...), read the actually-wired inputs (types, data-tree shape, samples), write a typed CPython 3 / IronPython 2 component matched to that data, convert the socket in place (convert_staged) or build and wire from scratch, run it, read Grasshopper's runtime errors, and fix until green. Use for ANY Grasshopper Python work here; routes deeper API and dev-workflow questions to the rhino-grasshopper-dev and scripting-reference skills."
---

# The Wireify loop

You are co-developing on a **live Grasshopper canvas**. The `wireify` MCP server is your hands on that canvas: it sees the real document, the real components, and -- critically -- the **real data flowing on the wires**. Turn intent into a working, typed Python component on that canvas, and **let the canvas correct you** rather than guessing.

## What is loaded with you

This home ships two knowledge skills (composed by skillmeld -- see `PROVENANCE.md`). Lean on them; do not restate them:

- **`scripting-reference`** -- the deep Rhino/Grasshopper Python API cookbook (rhinoscriptsyntax, RhinoCommon, ghpythonlib): curves, surfaces, meshes, Breps, transforms, analysis. Reach for it when you need *how-to code or which-API*.
- **`rhino-grasshopper-dev`** -- GH development workflow, conventions, input validation, GUID/public-surface preservation, packaging. Reach for it for dev/build/deploy questions and its validation discipline.

**One override:** where those skills describe live automation through the community **RhinoMCP** server or `run_python`, that does **not** apply here. In a Wireify home, **all live canvas automation goes through the `wireify` MCP tools** (listed at the end) -- different tools, same job.

## Two rules above all

1. **Never guess the data. Read it.** `read_input_data` reports the exact types, data-tree shape, and sample values on the wire. Type the script to *that*, not to what you assume.
2. **Never guess the runtime idioms.** CPython 3 and IronPython 2 are different languages. Mixing them is the most common way AI-written Grasshopper Python fails. Pick one per component and stay inside its rules (below).

## Data questions -- answer, do not build

Not every request is a component. If the user asks for **information** -- values, a JSON dump, "what is on this wire", a summary -- the whole job is: resolve the component, `read_input_data` on the relevant inputs (raise `maxPerBranch`/`maxTotal` to cover the tree -- check `tree.dataCount` and read again if the first caps truncated; the reads may go batched in one message), and **answer directly in chat**. For a staged socket, `get_document_summary(includeStagedData: true)` often answers in one call. No component, no `convert_staged`, no reference-skill excursions. One or two batched reads and a formatted answer is the correct shape of that session.

**Orient before you ask.** In this home, vague nouns ("the lines", "the tags", "the points") refer to the live canvas by default. When a request is ambiguous, spend ONE `get_document_summary(includeStagedData: true)` FIRST -- the canvas usually disambiguates (staged input names, wired data, W-numbers). Ask a clarifying question only if the canvas does not resolve it. Never open with a clarifying question you could have answered by looking.

**Budget rule for every request:** orientation plus reads should be under ~6 tool calls. If you catch yourself searching skill references or the install folder to figure out *what to do next* (rather than a specific API signature), stop -- re-read this skill's loop order and act, or tell the user plainly what is blocking. Burning tool calls on meta-investigation is the failure mode; the canvas is right there.

## Numbered sockets -- the primary flow

The user stages work on **Wireify sockets**: Merge-like components with a number badge (`W1`, `W2`, ...) whose inputs they wire and rename on the canvas. "do #3: cull panels below min_area" means socket 3.

1. **Resolve the number AND read the data in one call.** `get_document_summary` with `includeStagedData: true` returns the `wireify` block -- each socket (`staged`, with its staged input names) and each converted component (`converted`), with ids -- plus the live data on every wired staged input (read_input_data shape, default caps). Never guess ids; resolve `#n` here. That one call is normally the whole orientation.
2. **Deeper reads only when needed.** Call `read_input_data` directly only when the inlined samples truncated something you must see (raise `maxPerBranch`/`maxTotal`). Read-only calls may be batched in one message -- they serialize safely on Rhino's UI thread in milliseconds. Never batch a mutation with anything.
3. **Write plain script-mode Python** that reads the staged input names as variables and assigns every output you will declare. No `RunScript`, no class -- top-level statements. The params are built for you; the script just uses them.
4. **`convert_staged`** (socket id, code, `outputs`, optional `inputs`, optional kebab `nicknameSlug`). Params are built EXPLICITLY -- nothing is parsed from your code:
   - `outputs` (required): `[{name, access}]` in order, e.g. `[{"name": "points", "access": "list"}, {"name": "labels", "access": "list"}]`. Access: a single value -> `item`, a sequence -> `list`, branch-structured -> `tree`. A list assigned to an `item` output wraps into one opaque goo -- size access to what the script produces.
   - `inputs` (optional): access (+ optional type hint) per staged name, chosen from what `read_input_data` showed, e.g. `[{"name": "in1", "access": "list"}, {"name": "in2", "access": "list"}]`. Omit entirely for all-tree.
   It swaps the socket for a stock Python component in place -- wires migrated onto the same-named inputs, `W<n>` nickname kept, one undo step, solved -- **and the result carries that first solve's runtime report** (messages + output values). A refusal means your specs did not line up (it says exactly what) and NOTHING changed: fix the arguments, call again.
5. **Read the report you already have.** The convert result's `report` is the post-solve truth -- no `read_runtime_errors` call needed. Red? Fix with `set_source` (it recompiles, solves, and returns the next report in the same call; use `set_io` if the I/O itself must change).
6. **Leash:** two failed `convert_staged`/`set_io` calls in a row -> STOP. Report the tool's exact response to the user verbatim and ask how to proceed. Do not iterate script shapes or search references hoping the third try lands.

**Revising** a converted component ("revise #3: ...") is ONE `set_source` call in place on the resolved id -- it recompiles, solves, and returns the runtime report with fresh outputs, so a normal revise is a single mutation with its verification built in. Never create a new component or another socket for a revision. Keep the `W<n> ` nickname prefix if you rename anything; the number is how the user addresses it.

Only when there is **no socket** for the task (the user asks in prose, or wants an unstaged helper) use the from-scratch path in the loop below (`create_python_component` + `set_typed_io` + `set_source` + `wire`).

## The loop -- always this order

1. **Orient.** Make sure this definition's lessons are in context: newer homes import `MEMORY.md` straight into `CLAUDE.md` (you already have it); if not imported, Read `MEMORY.md` at the home root first (it is a DIFFERENT file from Claude Code's own auto-memory, which stays empty here). Then ONE `get_document_summary` -- with `includeStagedData: true` when the task names a socket -- plus, only as needed, `introspect_selected` / `get_runtime_info` (these read-only calls may be batched in one message).
2. **Deeper reads only if the inlined data truncated.** `read_input_data` with raised caps on the specific inputs (or the upstream outputs you will consume). Decide item/list/tree access and the Python types from real data, never assumption.
3. **Build, typed.** Socket staged? -> `convert_staged` (the whole build in one step, above -- returns the first solve's report). From scratch? -> `create_python_component` (`runtime`, default `cpython3`) -> **`set_io`** (declare inputs + outputs explicitly: name, access, optional hint -- plain script-mode NEVER derives params from source) -> `set_source` (plain top-level Python reading those names; it auto-prepends `#! python 3` on CPython 3 -- do not add it yourself; never add it for IronPython 2). `set_typed_io` is SDK-mode only (syncs params from a `RunScript` signature) -- do not use it on plain scripts.
4. **Wire.** Socket conversions arrive wired. Otherwise `wire` the upstream outputs into your new inputs (and outputs downstream where useful).
5. **Read the report the mutation returned.** `convert_staged`, `set_source`, and `run` all return the post-solve runtime report (messages + fresh outputs) -- that is your verification, already in hand. If red, diagnose and `set_source` again (idempotent -- reuse the same component); each fix returns its own report. Let the actual error drive the fix.
6. **`run` and `read_runtime_errors` are for the exceptions.** `run` (a background Task -- poll it, cancel a runaway) when you skipped the inline solve (`solve: false` on a heavy canvas) or must re-solve without a source change; `read_runtime_errors` only to re-check later without solving, or with `includeDocument` for canvas-wide messages.
7. **Record the lesson.** Append what broke + what fixed it to this project's `MEMORY.md`. Each `.gh` compounds its own knowledge.

Keep mutations tight and sequential -- one mutation, read its report, then the next; never batch mutations or mix a mutation with reads in one message. Read-only calls may be batched together (the server serializes them safely). Do not batch speculative changes.

**If a call errors with "UI thread busy" / "NOT started":** Rhino is momentarily blocked (long solve, modal dialog) and the call did NOT run. Wait a few seconds for Rhino to go idle, retry ONCE, and if it fails again report the exact error to the user and stop -- never hammer retries.

## Reading wired inputs (the edge)

`read_input_data` returns the payload below per input; `get_document_summary(includeStagedData: true)` inlines the same payload for every wired staged-socket input, so a socket task rarely needs the standalone reads:

```json
{ "param": "x", "access": "tree",
  "tree": { "pathCount": 4, "dataCount": 312, "isFlat": false },
  "types": [ {"typeName": "Number", "clr": "System.Double", "count": 300},
             {"typeName": "Line",   "clr": "Rhino.Geometry.Line", "count": 12} ],
  "samples": [ {"path": "{0;0}", "value": "3.14", "typeName": "Number"} ] }
```

- **`types`** is a histogram of what is actually on the wire. One CLR type -> type the input to it. Mixed -> handle it explicitly, or narrow with the user. Do not assume homogeneity.
- **`tree`** decides access: `pathCount == 1` + `isFlat`, many items -> **list** (or **item** for a single value); `pathCount > 1` -> **tree**.
- **`samples`** are ground truth for shape, units, and range -- catch radians-vs-degrees, mm-vs-m, empty branches, and nulls before writing logic.
- Values are valid only **after a solve**. If data looks stale or empty, run first, then read.

## Data trees

- Choose access deliberately: **item** (one value), **list** (one branch), **tree** (many branches). Wrong access is a top cause of red components and silent data loss.
- **Preserve tree structure** unless the task explicitly wants it flattened or grafted. Silently flattening a tree destroys branch information the rest of the definition depends on.
- For tree access, iterate branches by path and keep input/output paths aligned unless deliberately restructuring -- and when you restructure, do it explicitly in code and say so.

## Dual-runtime correctness (the wedge)

Two runtimes, not interchangeable. `get_runtime_info` shows what is available; `runtime` on `create_python_component` selects it.

**Runtime preflight, once per session, BEFORE the first create/convert:** check `get_runtime_info`. If `cpython3` is not in the available runtimes, STOP and tell the user plainly (their Rhino/RhinoCode install is the issue -- do not retry or improvise around it). If it is available but `rhinoCodeLoaded` is false, warn the user the first Python-3 component load can take a moment (RhinoCode initialises lazily; on some machines it is fragile) -- then proceed. Never spam creates against a runtime that is not there.

**CPython 3** (Rhino 8 SR18+, the default -- prefer it): real Python 3 (f-strings, `range`, true division, `except E as e:`, `print(...)`). Imports `import Rhino.Geometry as rg`, `import rhinoscriptsyntax as rs`, `import Grasshopper`, `import ghpythonlib.components as ghcomp`. `set_source` auto-prepends `#! python 3` -- required; without it the component silently loses its language spec and fails at solve.

**IronPython 2** (Rhino 7, or legacy GhPython on Rhino 8): Python 2.7 on .NET -- **no f-strings**, `xrange`, `print` statement (or `from __future__ import print_function`), integer `/` floor division, `except E, e:`. No `#! python 3` directive. .NET interop via `clr`.

**Never mix.** No f-strings in IronPython 2; no Python-2 `print` statement or `except E, e:` in CPython 3. Unsure which a snippet targets? Stop and check `get_runtime_info` -- a mismatch is a guaranteed red component. For exact API signatures in either runtime, consult `scripting-reference`.

To **modernise an IronPython 2 component to CPython 3**, use the `wireify-port` skill: read the old code, introspect its live inputs, regenerate as typed CPython 3, run both in parallel, diff outputs, fix until equivalent.

## Conventions the loop must get right

- **`# r:` pip header (CPython 3 only).** To use a pip package in a Rhino 8 CPython 3 component, declare it near the top: `# r: numpy` (or pinned, `# r: numpy==1.26`), one requirement per line. Rhino installs it on first run. Never use `# r:` in IronPython 2.
- **Typed I/O setup.** For every input and output set name, **access (item / list / tree)**, and a type hint where it sharpens correctness -- matched to what `read_input_data` reported. A typed input gives clean Python objects instead of generic goo; the right access prevents silent data-tree loss. Keep output access consistent with what you produce (a list output needs list access).

## Running and fixing

- **The runtime report comes to you.** `convert_staged`, `set_source`, and `run` return it (messages + fresh output values); a separate `read_runtime_errors` is only for re-checking without a solve or for `includeDocument` sweeps.
- The report is the truth: `Error` = it failed; `Warning`/`Remark` = ran with caveats. Read the message literally and fix the specific cause -- wrong access, a runtime-idiom mismatch, an unhandled null/empty branch, a units/tolerance mismatch, a missing import or `# r:` line.
- `run` is a **Task**: it returns a handle; poll for completion, cancel a runaway. Reach for it when you passed `solve: false` (heavy canvas) or need a re-solve without a source change.
- `set_source` is idempotent -- fix in place on the same component; never spawn a duplicate component to try a variant.

## Memory and conventions

This project's `CLAUDE.md` and the shared `~/.ify/wireify/defaults.md` carry standing conventions -- units, tolerance, go-to libraries, naming, and (where set) structural/AEC semantics. Honour them.

`MEMORY.md` at the home root is this definition's lesson ledger. **Read it at session start** (the loop's orient step) -- it is the prior context here, and it is NOT the same file as Claude Code's auto-memory (`~/.claude/projects/.../memory/MEMORY.md`), which nothing in a Wireify home writes. When the user asks what you know or remember about this definition, the answer comes from reading `./MEMORY.md` -- never report "memory is empty" without having read it. After a non-obvious fix, append a short lesson there (symptom, cause, fix), then read the file's tail back once to confirm the append landed.

## Tool reference (this project's `wireify` server)

- Orient (read-only, batchable): `get_document_summary` (the `wireify` numbered registry; `includeStagedData: true` inlines staged input data), `introspect_selected`, `introspect_component`, `read_input_data`, `get_runtime_info`, `get_source` (read an existing script component's code).
- Build (one at a time, each returns its own verification): `convert_staged` (socket -> stock Python component, explicit I/O, wires kept, + first solve's report), `create_python_component`, `set_io` (explicit I/O for plain script-mode), `set_source` (recompile + solve + report; `solve: false` to defer), `wire`; `set_typed_io` (SDK-mode `RunScript` sync only).
- Run / re-check: `run` (Task, returns the report), `read_runtime_errors` (re-read without solving; `includeDocument` for the whole canvas).
