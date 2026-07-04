# Rhino 8 Python 3 Grasshopper Scripts And Script Plugins

## Choosing Python 3

Use Rhino 8 Python 3 script components for fast iteration, research tooling, geometry prototypes, and workflows that benefit from CPython packages. Prefer compiled C# `.gha` components when the tool needs stable distribution, stronger typing, lower overhead, or a larger maintainable API.

## Script Component Practices

For a group of connected script components, design the Grasshopper data flow before writing scripts. Decide which component owns each operation, what intermediate geometry/data contracts are passed between components, whether outputs are item/list/tree, and how tree paths should be preserved or intentionally changed. Avoid exposing the same control input repeatedly across many components when a cleaner upstream component or shared structured output would make the workflow easier to wire and review.

- Name inputs and outputs explicitly.
- Set type hints for every input.
- Mark inputs as required when a missing value makes the result invalid.
- Set access mode deliberately: item, list, or tree.
- Avoid reserved names and ambiguous one-letter names except for very small experiments.
- Keep Grasshopper outputs typed and predictable.
- Return warnings through `out` or a dedicated message output when the script is used as a user-facing tool.
- Avoid baking, document mutation, file writes, and long-running package installs inside ordinary recomputation paths unless explicitly requested.

## Python Code Style

- Put reusable logic into functions with type hints.
- Validate `None`, empty input, non-finite numbers, invalid geometry, and units at the top of the script.
- Prefer RhinoCommon geometry for precise operations; use `rhinoscriptsyntax` for simple document automation.
- Keep unit conversions explicit.
- Set random seeds for randomized layout, optimization, sampling, or figures.
- Separate geometry generation from engineering checks when a script mixes design and calculation.

## Packages And Assemblies

Rhino 8 script components can reference PyPI packages and .NET assemblies from the script source. Use this only when the dependency is justified and portable.

Examples:

```python
# requirements: numpy
```

```python
# r: System.Text.Json.dll
```

Pin versions when reproducibility matters. Avoid heavy scientific, GUI, cloud, or hardware dependencies without user approval unless the project already uses them.

## Publishing Scripts As Plugins

For durable script plugins, use Rhino 8 ScriptEditor projects rather than copying loose code into many Grasshopper definitions.

Preferred path:

1. Prototype as a Python 3 or C# script component.
2. Stabilize input/output names, access modes, required inputs, descriptions, and type hints.
3. Move to a ScriptEditor project when the component needs to be shared.
4. Publish to `.gha`, `.rhp`, or Yak through ScriptEditor or `rhinocode`.
5. Build a generated `.sln` only when deeper customization is needed.

Terminal publishing uses Rhino's `rhinocode` utility, when available:

```bash
rhinocode project build /path/to/MyProject.rhproj
```

If `rhinocode` is unavailable in the shell, use Rhino's ScriptEditor UI and report that terminal publishing was not run.

## Review Checklist

- Are input/output names, type hints, access modes, and required flags explicit?
- For connected scripts, is the complete workflow data flow clear and economical?
- Do upstream outputs connect cleanly to downstream inputs without implicit flattening, grafting, unit conversion, or type coercion?
- Does the script preserve Grasshopper data-tree intent?
- Are package requirements encoded in the script or project rather than remembered manually?
- Are Rhino document units and tolerances explicit when they affect geometry or engineering interpretation?
- Is the script safe to recompute repeatedly?
- Does the published plugin path produce a `.gha`, `.rhp`, or `.yak` artifact as expected?
