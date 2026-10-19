## 6. Python for Revit

```python
# Available globals in RPS:
# __revit__  → Autodesk.Revit.UI.UIApplication
# doc        → Autodesk.Revit.DB.Document (active document)
# uidoc      → Autodesk.Revit.UI.UIDocument
# app        → Autodesk.Revit.ApplicationServices.Application

import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
from Autodesk.Revit.DB import *
from Autodesk.Revit.UI import *
```

### pyRevit Script Structure

```python
"""pyRevit script: Select All Walls on Active View"""
# -*- coding: utf-8 -*-
__title__ = "Select Walls"
__author__ = "AEC Scripter"

from pyrevit import revit, DB, UI, script, forms

doc = revit.doc
uidoc = revit.uidoc
output = script.get_output()

# Collect all walls in active view
walls = DB.FilteredElementCollector(doc, doc.ActiveView.Id)\
          .OfClass(DB.Wall)\
          .WhereElementIsNotElementType()\
          .ToElements()

output.print_md("## Found {} walls".format(len(walls)))
for w in walls:
    output.print_md("- **{}** | Width: {} | Level: {}".format(
        w.Name,
        w.Width,
        doc.GetElement(w.LevelId).Name
    ))
```

### Dynamo Python Script Node

```python
# CPython 3 node (Revit 2023+)
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitServices')
clr.AddReference('RevitNodes')

from Autodesk.Revit.DB import *
from RevitServices.Persistence import DocumentManager
from RevitServices.Transactions import TransactionManager

doc = DocumentManager.Instance.CurrentDBDocument

# Inputs from Dynamo
level_name = IN[0]
```

```python
# All walls in document
walls = FilteredElementCollector(doc)\
        .OfClass(Wall)\
        .WhereElementIsNotElementType()\
        .ToElements()

# All floor plan views
plans = FilteredElementCollector(doc)\
        .OfClass(ViewPlan)\
        .WhereElementIsNotElementType()\
        .ToElements()

# All family instances of a specific category
columns = FilteredElementCollector(doc)\
          .OfCategory(BuiltInCategory.OST_StructuralColumns)\
          .OfClass(FamilyInstance)\
          .ToElements()

# Elements in a specific view
view_elements = FilteredElementCollector(doc, active_view.Id)\
                .WhereElementIsNotElementType()\
                .ToElements()

# Using parameter filters
provider = ParameterValueProvider(ElementId(BuiltInParameter.WALL_BASE_CONSTRAINT))
rule = FilterStringRule(provider, FilterStringEquals(), "Level 1")
param_filter = ElementParameterFilter(rule)
walls_on_level1 = FilteredElementCollector(doc)\
                  .OfClass(Wall)\
                  .WherePasses(param_filter)\
                  .ToElements()

# Rooms
rooms = FilteredElementCollector(doc)\
        .OfClass(SpatialElement)\
        .OfCategory(BuiltInCategory.OST_Rooms)\
        .ToElements()
```

### Parameter Read/Write

```python
# Read parameters
wall = walls[0]

# Built-in parameter
base_offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()

# Named parameter (shared or project)
param = wall.LookupParameter("Fire Rating")
if param:
    if param.StorageType == StorageType.String:
        value = param.AsString()
    elif param.StorageType == StorageType.Double:
        value = param.AsDouble()
    elif param.StorageType == StorageType.Integer:
        value = param.AsInteger()
    elif param.StorageType == StorageType.ElementId:
        value = param.AsElementId()

# Write parameter (must be inside a Transaction)
t = Transaction(doc, "Set Fire Rating")
t.Start()
try:
    for wall in walls:
        param = wall.LookupParameter("Fire Rating")
        if param and not param.IsReadOnly:
            param.Set("2 Hour")
    t.Commit()
except Exception as e:
    t.RollBack()
    print("Error: {}".format(e))
```

```python
# Simple pattern
t = Transaction(doc, "Create Walls")
t.Start()
try:
    # ... Revit operations ...
    t.Commit()
except:
    t.RollBack()
    raise

# Transaction group (for undoable groups)
tg = TransactionGroup(doc, "Batch Operation")
tg.Start()
# ... multiple transactions ...
tg.Assimilate()  # or tg.RollBack()

# Sub-transaction (within an open Transaction)
st = SubTransaction(doc)
st.Start()
# ... operations ...
st.Commit()  # or st.RollBack()
```

```python
# 1. Create a wall
line = Line.CreateBound(XYZ(0,0,0), XYZ(20,0,0))
level = FilteredElementCollector(doc).OfClass(Level).FirstElement()
wall_type = FilteredElementCollector(doc).OfClass(WallType).FirstElement()

t = Transaction(doc, "Create Wall")
t.Start()
wall = Wall.Create(doc, line, wall_type.Id, level.Id, 10.0, 0.0, False, False)
t.Commit()

# 2. Place family instance
symbol = FilteredElementCollector(doc)\
         .OfClass(FamilySymbol)\
         .FirstElement()
t = Transaction(doc, "Place Family")
t.Start()
if not symbol.IsActive:
    symbol.Activate()
instance = doc.Create.NewFamilyInstance(XYZ(10,10,0), symbol,
                                        level, StructuralType.NonStructural)
t.Commit()

# 3. Create floor
curve_loop = CurveLoop()
curve_loop.Append(Line.CreateBound(XYZ(0,0,0), XYZ(20,0,0)))
curve_loop.Append(Line.CreateBound(XYZ(20,0,0), XYZ(20,15,0)))
curve_loop.Append(Line.CreateBound(XYZ(20,15,0), XYZ(0,15,0)))
curve_loop.Append(Line.CreateBound(XYZ(0,15,0), XYZ(0,0,0)))

floor_type = FilteredElementCollector(doc).OfClass(FloorType).FirstElement()
t = Transaction(doc, "Create Floor")
t.Start()
floor = Floor.Create(doc, [curve_loop], floor_type.Id, level.Id)
t.Commit()

# 4. Create sheet and place views
t = Transaction(doc, "Create Sheet")
t.Start()
titleblock = FilteredElementCollector(doc)\
             .OfClass(FamilySymbol)\
             .OfCategory(BuiltInCategory.OST_TitleBlocks)\
             .FirstElement()
sheet = ViewSheet.Create(doc, titleblock.Id)
sheet.Name = "Floor Plans"
sheet.SheetNumber = "A-101"

# Place view on sheet
vp = Viewport.Create(doc, sheet.Id, plan_view.Id, XYZ(1.0, 0.75, 0))
t.Commit()

# 5. Export to DWG
options = DWGExportOptions()
options.MergedViews = True
views_to_export = List[ElementId]()
views_to_export.Add(active_view.Id)
doc.Export("C:\\Export", "output.dwg", views_to_export, options)

# 6. Get room boundaries
for room in rooms:
    options = SpatialElementBoundaryOptions()
    boundaries = room.GetBoundarySegments(options)
    for loop in boundaries:
        for seg in loop:
            curve = seg.GetCurve()
            # process boundary curve

# 7. Color elements by parameter value
t = Transaction(doc, "Color by Value")
t.Start()
ogs = OverrideGraphicSettings()
ogs.SetProjectionLineColor(Color(255, 0, 0))
for wall in walls:
    if wall.LookupParameter("Fire Rating").AsString() == "2 Hour":
        doc.ActiveView.SetElementOverrides(wall.Id, ogs)
t.Commit()

# 8. Schedule data extraction
schedules = FilteredElementCollector(doc)\
            .OfClass(ViewSchedule)\
            .ToElements()
for sched in schedules:
    table = sched.GetTableData()
    section = table.GetSectionData(SectionType.Body)
    for row in range(section.NumberOfRows):
        for col in range(section.NumberOfColumns):
            cell = sched.GetCellText(SectionType.Body, row, col)
```
