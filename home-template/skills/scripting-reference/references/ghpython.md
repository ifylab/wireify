## 4. GhPython (Python in Grasshopper)

### Component Setup

In the GhPython component (or Script component in GH2):

- **Inputs**: Right-click each input to set Access (Item / List / Tree) and Type Hint (Point3d, Curve, float, str, etc.)

- **Outputs**: Name outputs at the bottom of the component

- **Type hints** are critical: without them, all inputs arrive as `IGH_Goo` wrappers

```python
import Grasshopper as gh
from Grasshopper import DataTree
from Grasshopper.Kernel.Data import GH_Path

# Create a data tree
tree = DataTree[object]()
for i in range(5):
    path = GH_Path(i)
    for j in range(10):
        tree.Add(rg.Point3d(i*10, j*10, 0), path)

# Iterate a data tree
for i in range(tree.BranchCount):
    path = tree.Path(i)
    branch = tree.Branch(i)
    for item in branch:
        print(item)

# Flatten
flat = tree.AllData()

# Graft (each item in its own branch)
grafted = DataTree[object]()
for i, item in enumerate(flat_list):
    grafted.Add(item, GH_Path(i))

# Output
a = tree  # assign to output parameter 'a'
```

### Script Structure Pattern

```python
"""GhPython component: Generate Building Massing"""
import Rhino.Geometry as rg
import Grasshopper as gh
from Grasshopper import DataTree
from Grasshopper.Kernel.Data import GH_Path
import math

# ── INPUTS ──────────────────────────────
# footprint   : Curve (Item)
# floors      : int (Item)
# floor_h     : float (Item)
# setback     : float (Item)

# ── LOGIC ───────────────────────────────
masses = []
sections = []

for i in range(floors):
    z = i * floor_h
    offset_dist = setback * i

    plane = rg.Plane(rg.Point3d(0, 0, z), rg.Vector3d.ZAxis)

    if offset_dist > 0:
        offset = footprint.Offset(plane, -offset_dist, 0.01,
                                   rg.CurveOffsetCornerStyle.Sharp)
        if offset and len(offset) > 0:
            section = offset[0]
        else:
            break
    else:
        section = footprint.DuplicateCurve()
        xform = rg.Transform.Translation(0, 0, z)
        section.Transform(xform)

    sections.append(section)

    # Extrude this floor
    ext_vec = rg.Vector3d(0, 0, floor_h)
    srf = rg.Extrusion.Create(section, floor_h, True)
    if srf:
        masses.append(srf.ToBrep())

# ── OUTPUTS ─────────────────────────────
a = masses      # List of Breps
b = sections    # List of Curves
c = sum(rg.AreaMassProperties.Compute(m).Area for m in masses if m)  # Total facade area
```

### Performance with ghpythonlib

```python
import ghpythonlib.parallel as ghp

def process_point(pt):
    """Heavy operation per point."""
    circle = rg.Circle(rg.Plane(pt, rg.Vector3d.ZAxis), radius)
    return circle.ToNurbsCurve()

# Parallel map — uses all CPU cores
results = ghp.run(process_point, points, True)
```

### Sticky Dictionary for Persistence

```python
import scriptcontext as sc

# Store data between runs
if "my_cache" not in sc.sticky:
    sc.sticky["my_cache"] = expensive_computation()

cached = sc.sticky["my_cache"]
```
