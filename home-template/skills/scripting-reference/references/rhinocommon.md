## 3. Python for Rhino (RhinoCommon)

RhinoCommon is the full .NET geometry library. It gives precise control over every geometric operation.

```python
import Rhino
import Rhino.Geometry as rg
import Rhino.DocObjects as rd
import Rhino.Input as ri
import Rhino.RhinoDoc as doc

# Key classes in Rhino.Geometry:
# Point3d, Vector3d, Plane, Line, Arc, Circle, Polyline,
# NurbsCurve, PolylineCurve, BrepFace, BrepEdge,
# Surface, NurbsSurface, Brep, Mesh, Transform,
# BoundingBox, Interval, Point2d
```

### Point3d and Vector3d Operations

```python
# Creation
p1 = rg.Point3d(0, 0, 0)
p2 = rg.Point3d(10, 5, 3)
v1 = rg.Vector3d(1, 0, 0)
v2 = rg.Vector3d(0, 1, 0)

# Arithmetic
p3 = p1 + v1              # Point + Vector = Point
v3 = p2 - p1              # Point - Point = Vector
v4 = v1 + v2              # Vector + Vector = Vector
v5 = v1 * 5.0             # scalar multiplication
v6 = v1 / 2.0             # scalar division

# Vector operations
dot = v1 * v2                        # dot product (0 = perpendicular)
cross = rg.Vector3d.CrossProduct(v1, v2)  # cross product
v1.Unitize()                         # normalize in-place
unit = v1 / v1.Length                # manual unitize
length = v1.Length
angle = rg.Vector3d.VectorAngle(v1, v2)  # radians

# Distance
dist = p1.DistanceTo(p2)

# Static members
origin = rg.Point3d.Origin           # (0,0,0)
xaxis = rg.Vector3d.XAxis            # (1,0,0)
yaxis = rg.Vector3d.YAxis            # (0,1,0)
zaxis = rg.Vector3d.ZAxis            # (0,0,1)
```

```python
# Line
ln = rg.Line(p1, p2)
ln_crv = rg.LineCurve(ln)

# Circle
circle = rg.Circle(rg.Plane.WorldXY, 5.0)
circle_crv = circle.ToNurbsCurve()

# Arc
arc = rg.Arc(p1, p2, p3)  # through 3 points
arc_crv = arc.ToNurbsCurve()

# Polyline
pline = rg.Polyline([rg.Point3d(x, 0, 0) for x in range(11)])
pline_crv = pline.ToPolylineCurve()

# NurbsCurve by control points
pts = [rg.Point3d(i*10, (i%3)*5, 0) for i in range(6)]
nc = rg.NurbsCurve.Create(False, 3, pts)  # open, degree 3

# Interpolated curve
interp = rg.Curve.CreateInterpolatedCurve(pts, 3)

# Fillet / Chamfer
filleted = rg.Curve.CreateFilletCurves(crv1, t1, crv2, t2, radius,
                                        True, True, True, 0.001, 0.001)

# Offset
offsets = crv.Offset(rg.Plane.WorldXY, 5.0, 0.01,
                      rg.CurveOffsetCornerStyle.Sharp)

# Boolean on closed planar curves
union = rg.Curve.CreateBooleanUnion(curves, 0.001)
diff = rg.Curve.CreateBooleanDifference(crv1, crv2, 0.001)
inter = rg.Curve.CreateBooleanIntersection(crv1, crv2, 0.001)
```

### Surface and Brep Creation

```python
# Planar surface from closed curve
breps = rg.Brep.CreatePlanarBreps(closed_crv, 0.001)

# Extrusion
ext = rg.Extrusion.Create(profile_crv, height, cap=True)
ext_brep = ext.ToBrep()

# Loft
loft = rg.Brep.CreateFromLoft(curves, rg.Point3d.Unset, rg.Point3d.Unset,
                                rg.LoftType.Normal, False)

# Sweep
sweep1 = rg.Brep.CreateFromSweep(rail, section, closed=False, tol=0.001)

# NurbsSurface from point grid
srf = rg.NurbsSurface.CreateFromPoints(pts_2d_list, u_count, v_count,
                                         u_degree, v_degree)

# Boolean operations
union = rg.Brep.CreateBooleanUnion(breps, 0.001)
diff = rg.Brep.CreateBooleanDifference(brep1, brep2, 0.001)
inter = rg.Brep.CreateBooleanIntersection(brep1, brep2, 0.001)

# Planar surface from corner points
srf = rg.NurbsSurface.CreateFromCorners(p1, p2, p3, p4)
```

```python
mesh = rg.Mesh()
mesh.Vertices.Add(0, 0, 0)
mesh.Vertices.Add(10, 0, 0)
mesh.Vertices.Add(10, 10, 0)
mesh.Vertices.Add(0, 10, 0)
mesh.Faces.AddFace(0, 1, 2, 3)  # quad
mesh.Normals.ComputeNormals()
mesh.Compact()

# From Brep
meshes = rg.Mesh.CreateFromBrep(brep, rg.MeshingParameters.Default)

# Mesh operations
mesh.Weld(Math.PI)                # weld vertices
mesh.UnifyNormals()               # consistent normals
mesh.RebuildNormals()
vol = rg.VolumeMassProperties.Compute(mesh)
area = rg.AreaMassProperties.Compute(mesh)
```

```python
# Curve-Curve
events = rg.Intersect.Intersection.CurveCurve(crv1, crv2, 0.001, 0.001)
for e in events:
    pt = e.PointA
    param_a = e.ParameterA
    param_b = e.ParameterB
    is_overlap = e.IsOverlap

# Curve-Surface
events = rg.Intersect.Intersection.CurveSurface(crv, srf, 0.001, 0.001)
for e in events:
    pt = e.PointA

# Brep-Brep
result, curves, pts = rg.Intersect.Intersection.BrepBrep(brep1, brep2, 0.001)

# Mesh-Ray
ray = rg.Ray3d(origin_pt, direction_vec)
t = rg.Intersect.Intersection.MeshRay(mesh, ray)
if t >= 0:
    hit_pt = ray.PointAt(t)

# Line-Plane
result, t = rg.Intersect.Intersection.LinePlane(line, plane)
if result:
    hit = line.PointAt(t)

# Brep-Plane (section)
result, curves, pts = rg.Intersect.Intersection.BrepPlane(brep, plane, 0.001)
```

### Transform Class

```python
# Translation
xform = rg.Transform.Translation(rg.Vector3d(10, 0, 0))

# Rotation (angle in radians)
xform = rg.Transform.Rotation(Math.PI / 4, rg.Vector3d.ZAxis, rg.Point3d.Origin)

# Scale
xform = rg.Transform.Scale(rg.Point3d.Origin, 2.0)        # uniform
xform = rg.Transform.Scale(rg.Plane.WorldXY, 2.0, 1.0, 3.0) # non-uniform

# PlaneToPlane
xform = rg.Transform.PlaneToPlane(source_plane, target_plane)

# Mirror
xform = rg.Transform.Mirror(rg.Plane.WorldXY)

# Apply
obj_copy = obj.Duplicate()
obj_copy.Transform(xform)

# Combine transforms
combined = xform1 * xform2  # xform2 applied first
```

```python
# BoundingBox
bbox = obj.GetBoundingBox(True)
min_pt = bbox.Min
max_pt = bbox.Max
center = bbox.Center
diagonal = bbox.Diagonal
is_valid = bbox.IsValid

# Plane
plane = rg.Plane(origin, normal)
plane = rg.Plane(origin, x_axis, y_axis)
plane = rg.Plane.WorldXY
closest = plane.ClosestPoint(test_pt)

# Interval (parameter domain)
interval = rg.Interval(0.0, 1.0)
mid = interval.Mid
length = interval.Length
t = interval.ParameterAt(0.5)  # normalized
```
