## 2. Python for Rhino (rhinoscriptsyntax)

The `rhinoscriptsyntax` module (imported as `rs`) wraps RhinoCommon in friendly, high-level functions. It is the fastest way to automate Rhino.

```python
import rhinoscriptsyntax as rs
```

```python
# Points
pt = rs.AddPoint(0, 0, 0)
pt2 = rs.AddPoint([10, 5, 3])

# Lines
line = rs.AddLine([0,0,0], [10,0,0])
line2 = rs.AddLine(rs.GetPoint("Start"), rs.GetPoint("End"))

# Polylines
pts = [[0,0,0],[5,0,0],[5,5,0],[0,5,0],[0,0,0]]
pline = rs.AddPolyline(pts)

# Curves
crv = rs.AddCurve(pts, degree=3)  # NURBS curve
interp = rs.AddInterpCurve(pts)    # interpolated through points

# Circles and Arcs
circle = rs.AddCircle([0,0,0], 5.0)
arc = rs.AddArc3Pt([0,0,0], [10,0,0], [5,5,0])
ellipse = rs.AddEllipse([0,0,0], 10, 5)

# Rectangles
rect = rs.AddRectangle([0,0,0], 10, 5)

# Surfaces
srf = rs.AddSrfPt([[0,0,0],[10,0,0],[10,10,0],[0,10,0]])
pipe = rs.AddPipe(crv, 0, 1.0)
loft = rs.AddLoftSrf([crv1, crv2])
extrusion = rs.ExtrudeCurveStraight(crv, [0,0,0], [0,0,5])
revolve = rs.AddRevSrf(profile_crv, axis_line)

# Meshes
mesh = rs.AddMesh([[0,0,0],[1,0,0],[1,1,0],[0,1,0]], [[0,1,2,3]])

# Solids
box = rs.AddBox([[0,0,0],[10,0,0],[10,10,0],[0,10,0],
                  [0,0,5],[10,0,5],[10,10,5],[0,10,5]])
sphere = rs.AddSphere([0,0,0], 5.0)
cylinder = rs.AddCylinder([0,0,0], [0,0,10], 3.0)
cone = rs.AddCone([0,0,0], [0,0,10], 5.0)

# Text and annotations
text = rs.AddText("Building A", [0,0,0], height=2.0)
dot = rs.AddTextDot("Label", [5,5,0])
leader = rs.AddLeader([[0,0,0],[5,5,0],[10,5,0]])
dim = rs.AddLinearDimension([0,0,0], [10,0,0], [5,2,0])
```

```python
# Selection
objs = rs.GetObjects("Select objects")
obj = rs.GetObject("Select one curve", rs.filter.curve)
pt = rs.GetPoint("Pick a point")

# By layer
layer_objs = rs.ObjectsByLayer("Walls")

# By type
all_curves = rs.ObjectsByType(rs.filter.curve)
all_surfs = rs.ObjectsByType(rs.filter.surface)
all_meshes = rs.ObjectsByType(rs.filter.mesh)

# By group
group_objs = rs.ObjectsByGroup("FloorPlan")

# Properties
obj_type = rs.ObjectType(obj)         # integer type code
obj_name = rs.ObjectName(obj)         # user-assigned name
obj_layer = rs.ObjectLayer(obj)       # layer name
obj_color = rs.ObjectColor(obj)       # (R,G,B) tuple
```

```python
# Move
rs.MoveObject(obj, [10, 0, 0])
rs.MoveObjects(objs, [0, 5, 0])

# Rotate (angle in degrees)
rs.RotateObject(obj, [0,0,0], 45)
rs.RotateObject(obj, [0,0,0], 90, axis=[0,0,1])

# Scale
rs.ScaleObject(obj, [0,0,0], [2, 2, 2])         # uniform
rs.ScaleObject(obj, [0,0,0], [1, 1, 3])         # non-uniform

# Mirror
rs.MirrorObject(obj, [0,0,0], [0,1,0])

# Copy
copy = rs.CopyObject(obj, [10, 0, 0])
copies = rs.CopyObjects(objs, [5, 5, 0])

# Orient
rs.OrientObject(obj, [ref1, ref2], [target1, target2])

# Array
rs.ArrayLinear(obj, count=5, offset=[10,0,0])
rs.ArrayPolar(obj, count=6, angle=360, center=[0,0,0])
```

```python
# Create layers
rs.AddLayer("Architecture")
rs.AddLayer("Walls", color=(200, 50, 50), parent="Architecture")
rs.AddLayer("Columns", color=(50, 50, 200), parent="Architecture")

# Layer properties
rs.CurrentLayer("Walls")
rs.LayerColor("Walls", (255, 0, 0))
rs.LayerVisible("Furniture", False)
rs.LayerLocked("Base Plan", True)
rs.LayerPrintWidth("Walls", 0.5)

# Bulk operations
all_layers = rs.LayerNames()
rs.PurgeLayer("Temp Construction")

# Move objects between layers
for obj in rs.ObjectsByLayer("Old Layer"):
    rs.ObjectLayer(obj, "New Layer")
```

### User Interaction

```python
# Input
point = rs.GetPoint("Click a point")
string = rs.GetString("Enter building name", "Default")
number = rs.GetReal("Enter floor height", 3.0, 2.5, 6.0)
integer = rs.GetInteger("Number of floors", 5, 1, 100)
boolean = rs.GetBoolean("Options", ["Visible","Yes","No"], [True])

# Output
rs.MessageBox("Operation complete!")
print("Processed {} objects".format(len(objs)))

# Selection
rs.SelectObject(obj)
rs.SelectObjects(objs)
rs.UnselectAllObjects()
```

### Geometry Analysis

```python
length = rs.CurveLength(crv)
area = rs.SurfaceArea(srf)             # returns [area, error]
volume = rs.SurfaceVolume(closed_brep)  # returns [volume, error]
centroid = rs.SurfaceAreaCentroid(srf)  # returns [point, error]

dist = rs.Distance([0,0,0], [10,10,0])
angle = rs.Angle([0,0,0], [10,10,0])

bbox = rs.BoundingBox(obj)             # 8 corner points
is_closed = rs.IsCurveClosed(crv)
is_planar = rs.IsCurvePlanar(crv)
degree = rs.CurveDegree(crv)
domain = rs.CurveDomain(crv)
```

```python
# Batch rename objects by layer
for obj in rs.AllObjects():
    layer = rs.ObjectLayer(obj)
    idx = rs.ObjectsByLayer(layer).index(obj)
    rs.ObjectName(obj, "{}_{}".format(layer, idx))

# Export each layer to separate file
for layer in rs.LayerNames():
    objs = rs.ObjectsByLayer(layer)
    if objs:
        rs.SelectObjects(objs)
        rs.Command("_-Export \"{}.3dm\" _Enter".format(layer))
        rs.UnselectAllObjects()

# Generate grid of points
for x in range(0, 100, 10):
    for y in range(0, 80, 10):
        rs.AddPoint(x, y, 0)

# Offset all curves on a layer inward
for crv in rs.ObjectsByLayer("Site Boundary"):
    offsets = rs.OffsetCurve(crv, rs.CurveAreaCentroid(crv)[0], 3.0)
```
