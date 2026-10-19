## 8. Cross-Platform Code Patterns

```python
# CSV
import csv
with open("data.csv", "r") as f:
    reader = csv.DictReader(f)
    for row in reader:
        x, y = float(row["x"]), float(row["y"])

with open("output.csv", "w", newline="") as f:
    writer = csv.writer(f)
    writer.writerow(["ID", "Area", "Volume"])
    for item in results:
        writer.writerow([item.id, item.area, item.volume])

# JSON
import json
with open("config.json", "r") as f:
    config = json.load(f)

with open("results.json", "w") as f:
    json.dump(results, f, indent=2)

# XML (for IFC, gbXML, etc.)
import xml.etree.ElementTree as ET
tree = ET.parse("energy.xml")
root = tree.getroot()
for zone in root.findall(".//Zone"):
    name = zone.get("id")
```

```python
import requests
# GET
response = requests.get("https://api.openweathermap.org/data/2.5/weather",
                         params={"lat": 40.7, "lon": -74.0, "appid": key})
data = response.json()

# POST (e.g., to Speckle)
response = requests.post("https://speckle.xyz/api/streams",
                          headers={"Authorization": "Bearer " + token},
                          json={"name": "My Model"})
```

### Geometry Libraries Comparison

| Operation | RhinoCommon | Revit API | shapely | trimesh |
|-----------|-------------|-----------|---------|---------|
| Point | Point3d | XYZ | Point | — |
| Line | LineCurve | Line | LineString | — |
| Polygon | PolylineCurve | CurveLoop | Polygon | — |
| Extrude | Extrusion.Create | — | — | trimesh.creation |
| Boolean | Brep.CreateBoolean* | BooleanOperationsUtils | .union/.difference | trimesh.boolean |
| Intersect | Intersection.* | Face.Intersect | .intersection | — |
| Area | AreaMassProperties | SpatialElement.Area | .area | mesh.area |
| Mesh | Mesh class | — | — | Trimesh class |

```python
# Python general
try:
    result = risky_operation()
except Exception as e:
    print("Error: {}".format(e))
    # Log, recover, or re-raise

# RhinoCommon-safe pattern
breps = rg.Brep.CreateBooleanUnion(inputs, 0.001)
if breps is None or len(breps) == 0:
    # Boolean failed — common with bad geometry
    print("Boolean union failed. Check input geometry.")
    breps = inputs  # fallback to originals

# Revit transaction safety
t = Transaction(doc, "Operation")
t.Start()
try:
    # operations
    t.Commit()
except Exception as e:
    if t.HasStarted() and not t.HasEnded():
        t.RollBack()
    raise
```
