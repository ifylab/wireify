# C# GHA And RhinoCommon Component Guidance

## Runtime And Project Setup

Choose the target based on the actual support requirement:

- Rhino 8 on macOS only: target Rhino 8 .NET Core, commonly `net7.0` unless the project explicitly requires a newer Rhino 8 runtime.
- Rhino 8 on Windows with default runtime: target .NET Core.
- Rhino 8 on Windows with .NET Framework mode or Rhino 7 compatibility: include `net48`.
- Cross-runtime Rhino 8 work: multi-target `net48;net7.0` when the dependencies support it.

Prefer McNeel templates or NuGet packages over hardcoded DLL paths. If using `PackageReference`, avoid copying RhinoCommon, Grasshopper, GH_IO, Eto, or Rhino.UI into the plugin output. In project review, flag direct `HintPath` references unless the repository has a deliberate reason.

## Component Contract

For a plugin with multiple connected components, design the graph contract before individual classes:

- Define the intended Grasshopper workflow from source geometry/data to final outputs.
- Choose component boundaries that match meaningful design or engineering operations, not arbitrary code units.
- Minimize repeated upstream inputs by passing structured intermediate outputs where that improves usability.
- Name inputs and outputs so downstream linkages are obvious on the canvas.
- Specify whether each output is item, list, or tree and whether paths are preserved, grouped, or intentionally reshaped.
- Keep optional inputs local to the component that actually needs them.

Compiled Grasshopper components should:

- Inherit from `Grasshopper.Kernel.GH_Component`.
- Provide a public empty constructor that calls the base constructor with full name, nickname, description, category, and subcategory.
- Register all inputs and outputs explicitly in `RegisterInputParams` and `RegisterOutputParams`.
- Use `GH_ParamAccess.item`, `.list`, or `.tree` deliberately.
- Keep `ComponentGuid` unique and stable. Do not change a released component GUID.
- Use `GH_AssemblyInfo` when the plugin needs assembly-level metadata, package restore identity, or clearer discoverability.

## SolveInstance Rules

In `SolveInstance`:

- Use `DA.GetData`, `DA.GetDataList`, or `DA.GetDataTree` to match parameter access.
- Return immediately when required input is missing.
- Validate nulls, invalid RhinoCommon geometry, empty collections, tolerance assumptions, units, coordinate system, and non-finite numerical values.
- Use `AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, ...)` for recoverable user-facing issues.
- Use `AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ...)` for invalid input that prevents a valid result.
- Avoid catching broad exceptions. If a catch is justified, catch a specific exception and report an actionable message.
- Keep document mutation, baking, file I/O, network calls, and long-running operations out of ordinary solve paths unless the component is explicitly designed for those effects.

## Data Trees

Do not flatten, graft, simplify, or re-path data trees just to make code easier. Preserve tree topology when it carries design intent.

- Use item access for independent per-item transforms.
- Use list access for branch-local processing.
- Use tree access only when cross-branch topology matters.
- For compiled components, prefer `GH_Structure<T>` where `T` is an `IGH_Goo` type when reading full trees.
- Document whether the output preserves input paths, creates one branch per input, or returns a flattened result.

## Geometry And Engineering Checks

For geometry-related engineering components:

- State Rhino document units or require explicit units in parameter names.
- Use SI units unless the project convention says otherwise.
- Encode units in names where practical, such as `length_mm`, `force_kN`, `stiffness_N_per_mm`, and `freq_Hz`.
- Make tolerances explicit and derive them from document tolerance only when that is the intended behaviour.
- Validate curve closure, planarity, Brep validity, mesh topology, orientation, and empty geometry before downstream computation.

## Review Checklist

- Are public component GUIDs unchanged?
- For multi-component plugins, is the whole data flow coherent from first input to final output?
- Do outputs from upstream components match the expected access mode, type, and tree topology of downstream inputs?
- Are component boundaries chosen to reduce canvas wiring complexity without hiding important engineering assumptions?
- Are component descriptions and parameter descriptions meaningful?
- Does every required parameter reject missing data clearly?
- Does the component report warnings/errors in Grasshopper rather than only throwing exceptions?
- Are numerical edge cases tested: zero, negative, non-finite, empty list, invalid geometry, and boundary tolerance?
- Is pure logic separated into testable classes rather than buried entirely in `SolveInstance`?
- Are Rhino/Grasshopper assemblies referenced but not copied locally?
- Does the build produce a `.gha` artifact and not only a `.dll`?

## Build Commands

Use the repository's documented build first. Common fallbacks:

```bash
dotnet restore
dotnet build -c Release
```

If NuGet packages are missing and network is restricted, report that restore/build could not be completed and do not pretend runtime validation happened.
