---
name: scripting-reference
description: Code patterns, example snippets, and API reference for computational-design scripting. Use when you need how-to code or which-library guidance for Python in Rhino and Grasshopper (RhinoCommon, rhinoscriptsyntax, ghpythonlib): creating NURBS curves and surfaces, looping over many mesh vertices for performance, or porting a wired Grasshopper definition into real reusable code. Also covers C# for Grasshopper components, Python for Revit (pyRevit), and JavaScript for web 3D (Three.js). The reference and example-code layer for writing the scripts themselves.
license: MIT
---

# Scripting Reference for AEC Computational Design

### Why Code Beyond Visual Programming

Visual programming environments like Grasshopper, Dynamo, and Marionette have democratized computational design for architects and engineers. But they hit hard walls in practice:

- **Control flow limitations**: Try writing a recursive space-partitioning algorithm in pure Grasshopper. You cannot. Loops require plugins (Anemone, Hoopsnake) that add fragility.

- **Performance**: Visual node evaluation carries overhead. A Python loop processing 50,000 mesh vertices runs 5-20x faster than equivalent wired-up components.

- **Data management**: Parsing CSV, calling REST APIs, reading databases, writing Excel reports — all trivial in code, painful or impossible in visual programming.

- **Reusability**: Functions, classes, modules, packages — code is composable at every scale.

- **Testing**: You can unit-test a Python function. You cannot unit-test a Grasshopper cluster.

### The Scripting Spectrum

| Level | Tool | Difficulty | Use Case |
|-------|------|------------|----------|
| Entry | rhinoscriptsyntax (rs) | Easy | Quick automation, batch ops, simple geometry |
| Intermediate | RhinoCommon (Python) | Medium | Complex geometry, intersections, analysis |
| Intermediate | GhPython | Medium | Custom GH components, data tree manipulation |
| Advanced | C# in GH | Hard | Performance-critical components, custom .gha |
| Advanced | pyRevit / Dynamo Python | Medium | Revit automation, BIM scripting |
| Advanced | Full .NET (C#/F#) | Hard | Production plugins, commercial tools |
| Specialist | Three.js / WebGL | Medium | Web viewers, interactive presentations |

### Choosing the Right Language

**Python** — Use when:

- Rapid prototyping and iteration matter most

- You need quick automation scripts (Rhino or Revit)

- Working with data (CSV, JSON, API calls)

- Algorithmic design exploration in Grasshopper

- Team includes non-programmers who need to read the code

**C#** — Use when:

- Performance is paramount (10-100x faster than Python for tight loops)

- Building distributable Grasshopper plugins (.gha)

- Integrating with .NET ecosystem libraries

**JavaScript/TypeScript** — Use when:

- Building web-based viewers and dashboards

- Integration with Speckle, IFC.js, or custom APIs

### Python vs. C# for AEC — A Practical Comparison

```
Feature               Python                    C#
─────────────────────────────────────────────────────────────
Typing                Dynamic                   Static
Speed                 1x (baseline)             10-100x
REPL                  Yes (interactive)         No (compile required)
GH Component          GhPython node             Scripting / Visual Studio
Learning curve        Gentle                    Steeper
Error detection       Runtime                   Compile-time
Deployment            .py files                 .gha / .dll
External libraries    pip (huge ecosystem)      NuGet (large ecosystem)
Data science          Excellent (numpy, pandas) Good (MathNet, ML.NET)
String handling       Superior                  Adequate
Memory management     Automatic (GC)            Automatic (GC)
Multithreading        GIL limitation            Full parallel support
```

## Reference files

The cookbook sits in `references/`, one file per section, read on demand: open the one the question is about (a signature, a pattern, a setup step) instead of the whole set.

- [2. Python for Rhino (rhinoscriptsyntax)](references/rhinoscriptsyntax.md) — User Interaction, Geometry Analysis
- [3. Python for Rhino (RhinoCommon)](references/rhinocommon.md) — Point3d and Vector3d Operations, Surface and Brep Creation, Transform Class
- [4. GhPython (Python in Grasshopper)](references/ghpython.md) — Component Setup, Script Structure Pattern, Performance with ghpythonlib, Sticky Dictionary for Persistence
- [5. C# for Grasshopper](references/csharp-grasshopper.md) — C# Scripting Component, Custom GH_Component Development, Building and Deploying .gha
- [6. Python for Revit](references/revit-python.md) — pyRevit Script Structure, Dynamo Python Script Node, Parameter Read/Write
- [7. JavaScript for Web 3D](references/threejs.md) — Raycasting for Selection, Materials for AEC
- [8. Cross-Platform Code Patterns](references/cross-platform-patterns.md) — Geometry Libraries Comparison
- [9. Development Environment Setup](references/environment-setup.md) — VS Code for Python, Visual Studio for C# GH Development, Git for AEC Scripts, RhinoCommon API Documentation, …

*This skill provides the scripting foundation for all AEC computational design work.*

*Wireify note: split from one file into this router plus `references/` at vendoring time; section text is unchanged. See `skills/PROVENANCE.md`, Local modifications.*
