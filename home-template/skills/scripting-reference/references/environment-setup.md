## 9. Development Environment Setup

### VS Code for Python

- Python (Microsoft)

- Rhino-Python (autocomplete stubs)

**Settings for Rhino Python:**

```json
{
    "python.analysis.extraPaths": [
        "C:\\Users\\<user>\\.rhinocode\\lib"
    ],
    "python.autoComplete.extraPaths": [
        "C:\\Users\\<user>\\.rhinocode\\lib"
    ]
}
```

**RhinoCommon stubs** install:

```bash
pip install Rhino-stubs
pip install Grasshopper-stubs
```

### Visual Studio for C# GH Development

1. Install Visual Studio 2022 Community

3. Create Class Library (.NET Framework) project

4. NuGet: `Install-Package Grasshopper`

5. Set post-build event: `copy "$(TargetPath)" "%APPDATA%\Grasshopper\Libraries\"`

6. Debug: attach to `Rhino.exe` process, set breakpoints

### Git for AEC Scripts

```bash
# .gitignore for AEC projects
*.3dm
*.rvt
*.rfa
*.dwg
*.ghx        # large GH files (keep .gh or .ghx, not both)
__pycache__/
*.pyc
.vs/
bin/
obj/
```

```bash
# Python (pip)
pip install compas compas_rhino compas_ghpython
pip install shapely trimesh open3d numpy scipy pandas

# C# (NuGet)
Install-Package Grasshopper
Install-Package MathNet.Numerics
Install-Package Newtonsoft.Json

# JavaScript (npm)
npm install three @types/three
npm install web-ifc web-ifc-three web-ifc-viewer
npm install @speckle/viewer
```

| Platform | Strategy |
|----------|----------|
| Rhino Python | `print()` to command line; `rs.MessageBox()` for breakpoints |
| GhPython | `print()` to GH panel; connect Panel component to output |
| C# GH Script | `Print()` and `Reflect.Component` for messages |
| C# Plugin | Attach Visual Studio debugger to Rhino.exe |
| pyRevit | `output.print_md()` for formatted output; `forms.alert()` |
| Dynamo | Watch nodes; Python `print()` to console |
| Three.js | Browser DevTools; `console.log()`; Scene inspector extensions |

### RhinoCommon API Documentation

- Official: https://developer.rhino3d.com/api/rhinocommon/

- Samples: https://github.com/mcneel/rhino-developer-samples

### Revit API Documentation

- Official SDK samples (installed with Revit SDK)

- The Building Coder blog: https://thebuildingcoder.typepad.com/
