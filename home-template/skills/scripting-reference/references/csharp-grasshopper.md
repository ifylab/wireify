## 5. C# for Grasshopper

### C# Scripting Component

```csharp
// Inputs:  pts (List<Point3d>), radius (double), height (double)
// Outputs: A (List<Brep>), B (double)

using Rhino.Geometry;
using System.Collections.Generic;
using System.Linq;

private void RunScript(List<Point3d> pts, double radius, double height,
                        ref object A, ref object B)
{
    var breps = new List<Brep>();

    foreach (var pt in pts)
    {
        var plane = new Plane(pt, Vector3d.ZAxis);
        var circle = new Circle(plane, radius);
        var crv = circle.ToNurbsCurve();
        var ext = Extrusion.Create(crv, height, true);
        if (ext != null)
            breps.Add(ext.ToBrep());
    }

    double totalVolume = breps
        .Select(b => VolumeMassProperties.Compute(b))
        .Where(v => v != null)
        .Sum(v => v.Volume);

    A = breps;
    B = totalVolume;
}
```

```csharp
using Grasshopper;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

var tree = new DataTree<Point3d>();

for (int i = 0; i < 5; i++)
{
    var path = new GH_Path(i);
    for (int j = 0; j < 10; j++)
    {
        tree.Add(new Point3d(i * 10, j * 10, 0), path);
    }
}

// Iterate
for (int i = 0; i < tree.BranchCount; i++)
{
    GH_Path path = tree.Path(i);
    List<Point3d> branch = tree.Branch(i);
    foreach (var pt in branch) { /* ... */ }
}
```

### Custom GH_Component Development

```csharp
using Grasshopper.Kernel;
using Rhino.Geometry;
using System;

public class BuildingMassComponent : GH_Component
{
    public BuildingMassComponent()
        : base("Building Mass", "BldgMass",
               "Generate stepped building massing",
               "AEC Tools", "Massing")
    { }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddCurveParameter("Footprint", "F", "Building footprint curve",
                                    GH_ParamAccess.item);
        pManager.AddIntegerParameter("Floors", "N", "Number of floors",
                                      GH_ParamAccess.item, 5);
        pManager.AddNumberParameter("Floor Height", "H", "Height per floor",
                                     GH_ParamAccess.item, 3.0);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddBrepParameter("Massing", "M", "Building massing breps",
                                   GH_ParamAccess.list);
        pManager.AddNumberParameter("GFA", "A", "Gross floor area",
                                     GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        Curve footprint = null;
        int floors = 5;
        double floorH = 3.0;

        if (!DA.GetData(0, ref footprint)) return;
        DA.GetData(1, ref floors);
        DA.GetData(2, ref floorH);

        var breps = new List<Brep>();
        double gfa = 0;

        for (int i = 0; i < floors; i++)
        {
            var moved = footprint.DuplicateCurve();
            moved.Transform(Transform.Translation(0, 0, i * floorH));
            var ext = Extrusion.Create(moved, floorH, true);
            if (ext != null)
            {
                breps.Add(ext.ToBrep());
                var area = AreaMassProperties.Compute(moved);
                if (area != null) gfa += area.Area;
            }
        }

        DA.SetDataList(0, breps);
        DA.SetData(1, gfa);
    }

    protected override System.Drawing.Bitmap Icon => null; // embed your 24x24 icon
    public override Guid ComponentGuid => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
    public override GH_Exposure Exposure => GH_Exposure.primary;
}
```

### Building and Deploying .gha

1. Create a Class Library (.NET Framework 4.8) project in Visual Studio

2. Add NuGet references: `Grasshopper` (includes RhinoCommon)

3. Set build output to `%APPDATA%\Grasshopper\Libraries\`

4. Build → the `.gha` file loads automatically when GH starts

5. For distribution, use the Yak package manager: `yak build` and `yak push`
