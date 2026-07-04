---
name: rhino-grasshopper-dev
description: Build, test, review, package, and deploy Rhino 8 and Grasshopper components and plugins, and write or generate the Python that runs inside a Grasshopper script component. Use when authoring a Rhino 8 Python 3 script component, developing a C# GHA plugin or RhinoCommon plugin, scaffolding a new .gha project, reviewing a Grasshopper definition, packaging a Yak, running smoke tests or CI, preserving released component GUIDs, or driving live Rhino and Grasshopper automation through RhinoMCP. The end-to-end workflow for producing and shipping a working Grasshopper script component or plugin.
license: MIT
---

# Rhino Grasshopper Dev

Use this skill for Rhino 8 and Grasshopper development work that needs repo-first inspection, source-of-truth McNeel guidance, and practical validation. Keep edits small, preserve released component GUIDs, and separate stable engineering logic from Grasshopper UI wrappers where possible. When creating a plugin or a set of connected components, design the workflow data flow first: define the upstream inputs, intermediate data contracts, downstream outputs, component linkages, data-tree topology, and user-facing parameter names before implementing individual components.

Pick the smallest set of references needed. The skill is structured so a single mode usually requires reading only one or two reference files:

- **C# GHA component or RhinoCommon plugin**: read `references/csharp-gha.md`. Use `scripts/scaffold_csharp_gha.py` only when starting a new small project or test fixture.

- **Rhino 8 Python 3 Grasshopper script or ScriptEditor plugin**: read `references/python3-script-plugins.md`.

- **Review, test, deploy, CI, Yak, package restore**: read `references/testing-and-deployment.md`.

- **Live Rhino or Grasshopper automation through MCP**: read `references/rhino-mcp.md`; prefer direct MCP tools when available and `run_python` for non-trivial RhinoCommon or Grasshopper API work.

1. Inspect the project before editing: `.sln`, `.csproj`, package references, build scripts, deploy scripts, `manifest.yml`, component classes, GUIDs, tests, and local instructions.

2. Identify the artifact type: script component, ScriptEditor project, compiled `.gha`, Rhino `.rhp`, Yak package, or live Rhino/Grasshopper canvas.

3. For plugins or connected component sets, map the whole workflow before coding: input sources, component boundaries, intermediate geometry/data types, tree branch semantics, optional versus required parameters, output consumers, and expected linkages on the Grasshopper canvas.

4. Preserve public surface: component names, nicknames, categories, parameter order, serialization data, file formats, GUIDs, and numerical behaviour unless the user explicitly asks to change them.

5. Validate inputs at numerical and geometry entry points: nulls, empty lists, tree structure, units, tolerances, coordinate system, invalid curves/Breps/meshes, and missing Rhino documents.

6. Report Grasshopper-facing failures with `AddRuntimeMessage` for C# components or clear output/error messages for scripts. Do not fail silently.

7. Run the most relevant checks: static inspection, `dotnet build`, unit tests, Yak build, Rhino/Grasshopper load test, RhinoMCP smoke test, or viewport/selection inspection.

Stop and obtain explicit user approval before:

- changing a released `ComponentGuid`, component name, nickname, category,
  parameter order, serialization, or public file format

- baking geometry, deleting objects, clearing Rhino/Grasshopper documents, or
  mutating a live model through MCP

- adding heavy dependencies, network calls, package installs, GUI libraries, or
  long-running work inside an ordinary Grasshopper solve path

## Utility Scripts

- `scripts/check_rhino_mcp.py`: verify a RhinoMCP HTTP endpoint and list exposed tools.

- `scripts/inspect_gha_project.py`: static inspection for common C# GHA project risks such as missing Grasshopper references, duplicate `ComponentGuid` values, and missing runtime messages.

- `scripts/scaffold_csharp_gha.py`: create a minimal C# Grasshopper component project for a first test or throwaway scaffold.

Run scripts with `python3` (3.10+ recommended) and read their `--help` output before use. They use only the standard library, are intentionally conservative, and do not modify existing projects unless an explicit output path is supplied.

- Does the project target the correct runtime for the user: Rhino 8 .NET Core on macOS, optional .NET Framework mode on Windows, or Rhino 7 compatibility?

- Are RhinoCommon and Grasshopper references sourced from NuGet or project templates rather than brittle machine-local paths?

- Are Rhino/Grasshopper assemblies excluded from output copying?

- Is every component GUID unique and stable?

- Are parameter names, nicknames, descriptions, access modes, and type hints explicit?

- For connected components, does each component have a clear role in the full workflow, with minimal duplicated inputs and outputs shaped for the next downstream component?

- Are linkages between components obvious from parameter names, access modes, and output data structures?

- Are geometry tolerances and document units explicit where they affect interpretation?

- Is deployment through Yak or a documented local development folder, with Rhino restart requirements stated?

## Failure Modes And Fallbacks

| Trigger | First action | Fallback |
|---|---|---|
| Project type unclear | Inspect `.sln`, `.csproj`, `.rhproj`, `.gha`, `.gh`, `manifest.yml` | State uncertainty; run source-level review only |
| Rhino/Grasshopper unavailable | Run static inspection, pure tests, and build/package checks | Report runtime checks as not performed |
| RhinoMCP unreachable | Run `scripts/check_rhino_mcp.py` | Do not edit live documents; provide the rerun command |
| MCP tools are minimal | Prefer `run_python` for non-trivial API work | Avoid graph assumptions; name available tools |
| `dotnet restore` or build fails | Report the command and first relevant error | Continue static review; do not claim `.gha` validation |
| Yak unavailable | Locate Rhino's bundled Yak or use project docs | Mark package build/publish as not performed |
| Edit changes public component surface | Stop at a proposed change list | Wait for approval |
| Sources conflict | Prefer McNeel docs, samples, and Discourse | Mark third-party advice as supplemental |

- change released component GUIDs or public parameter contracts without explicit
  approval

- flatten, graft, simplify, or re-path Grasshopper data trees just to make code
  easier

- bury testable engineering logic entirely inside `SolveInstance`

- copy RhinoCommon, Grasshopper, GH_IO, Eto, or Rhino.UI assemblies into plugin
  output unless the project has a documented reason

- catch broad exceptions and suppress them instead of surfacing actionable
  Grasshopper runtime messages

- mutate documents, bake geometry, write files, install packages, or make
  network calls during recomputation unless explicitly designed for it

- claim Rhino load, Grasshopper solve, viewport, Yak publish, or GUI validation
  unless that check was actually run
