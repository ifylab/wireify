// SPDX-License-Identifier: Apache-2.0
using System;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class HintSelectionTests
{
    // The live-confirmed registry shape (Rhino 8.29): concrete types only, no dynamic/object.
    static readonly string[] LiveHints =
    {
        "bool", "int", "string", "double", "Complex", "DateTime", "Color", "Point3d",
        "Point3dList", "Vector3d", "Plane", "Interval", "UVInterval", "Guid", "Box",
        "Transform", "Line", "Circle", "Arc", "Curve", "Polyline", "Rectangle3d", "Mesh",
        "Surface", "Extrusion", "SubD", "Brep", "PointCloud", "GeometryBase", "Hatch",
        "TextDot", "TextEntity", "Leader",
    };

    [Fact]
    public void Explicit_resolves_case_insensitively_to_the_registry_spelling()
    {
        Assert.Equal("Line", HintSelection.PickExplicit("line", LiveHints));
        Assert.Equal("string", HintSelection.PickExplicit("STRING", LiveHints));
    }

    [Fact]
    public void Explicit_trusts_the_request_when_the_registry_is_unreadable()
    {
        Assert.Equal("Line", HintSelection.PickExplicit("Line", Array.Empty<string>()));
    }

    [Fact]
    public void Explicit_throws_loudly_on_a_miss_naming_the_available_hints()
    {
        // The field-observed trap: Python names are not hint tokens.
        var ex = Assert.Throws<ArgumentException>(() => HintSelection.PickExplicit("str", LiveHints));
        Assert.Contains("'str' is not available", ex.Message);
        Assert.Contains("string", ex.Message);
    }

    [Fact]
    public void Explicit_rejects_empty_names()
    {
        Assert.Throws<ArgumentException>(() => HintSelection.PickExplicit("  ", LiveHints));
    }

    [Fact]
    public void Auto_hint_maps_the_common_clr_types_to_registry_tokens()
    {
        Assert.Equal("string", HintSelection.AutoHint("System.String"));
        Assert.Equal("double", HintSelection.AutoHint("System.Double"));
        Assert.Equal("int", HintSelection.AutoHint("System.Int32"));
        Assert.Equal("bool", HintSelection.AutoHint("System.Boolean"));
        Assert.Equal("Line", HintSelection.AutoHint("Rhino.Geometry.Line"));
        Assert.Equal("Point3d", HintSelection.AutoHint("Rhino.Geometry.Point3d"));
        Assert.Equal("Brep", HintSelection.AutoHint("Rhino.Geometry.Brep"));
    }

    [Fact]
    public void Auto_hint_folds_curve_and_surface_subclasses_to_their_base_tokens()
    {
        Assert.Equal("Curve", HintSelection.AutoHint("Rhino.Geometry.LineCurve"));
        Assert.Equal("Curve", HintSelection.AutoHint("Rhino.Geometry.NurbsCurve"));
        Assert.Equal("Curve", HintSelection.AutoHint("Rhino.Geometry.PolyCurve"));
        Assert.Equal("Surface", HintSelection.AutoHint("Rhino.Geometry.NurbsSurface"));
    }

    [Fact]
    public void Auto_hint_leaves_unknown_and_lossy_types_unmapped()
    {
        Assert.Null(HintSelection.AutoHint("System.Int64")); // lossy — the caller's explicit choice
        Assert.Null(HintSelection.AutoHint("Some.Plugin.CustomType"));
        Assert.Null(HintSelection.AutoHint(""));
    }

    [Fact]
    public void Every_auto_hint_token_exists_in_the_live_registry()
    {
        foreach (var clr in new[]
        {
            "System.String", "System.Double", "System.Int32", "System.Boolean", "System.Guid",
            "System.DateTime", "System.Drawing.Color", "Rhino.Geometry.Point3d",
            "Rhino.Geometry.Vector3d", "Rhino.Geometry.Plane", "Rhino.Geometry.Interval",
            "Rhino.Geometry.Line", "Rhino.Geometry.Circle", "Rhino.Geometry.Arc",
            "Rhino.Geometry.Polyline", "Rhino.Geometry.Box", "Rhino.Geometry.Transform",
            "Rhino.Geometry.Rectangle3d", "Rhino.Geometry.Mesh", "Rhino.Geometry.Brep",
            "Rhino.Geometry.SubD", "Rhino.Geometry.Extrusion", "Rhino.Geometry.PointCloud",
            "Rhino.Geometry.GeometryBase", "Rhino.Geometry.LineCurve", "Rhino.Geometry.NurbsSurface",
        })
        {
            var token = HintSelection.AutoHint(clr);
            Assert.NotNull(token);
            Assert.NotNull(HintSelection.Resolve(token!, LiveHints));
        }
    }

    [Fact]
    public void Resolve_returns_null_on_absent_tokens()
    {
        Assert.Null(HintSelection.Resolve("Dynamic", LiveHints));
        Assert.Equal("Curve", HintSelection.Resolve("curve", LiveHints));
    }

    [Fact]
    public void Mixed_tree_warning_names_the_param_the_types_and_both_ways_out()
    {
        var warning = HintSelection.MixedTreeWarning(
            "in1", new[] { "Rhino.Geometry.Line", "System.String" });

        Assert.Contains("'in1'", warning);
        Assert.Contains("Rhino.Geometry.Line and System.String", warning);
        Assert.Contains("own staged input", warning);
        Assert.Contains("scriptcontext", warning);
    }

    [Fact]
    public void Declared_hints_fill_empty_echo_fields_and_never_override_real_reads()
    {
        var introspection = new ComponentIntrospection(
            Guid.NewGuid(), "Script", "W2",
            new[]
            {
                new ParamInfo("in1", "in1", "list", "Generic Data", true),
                new ParamInfo("in2", "in2", "tree", "Generic Data", true, Hint: "Line"),
            },
            new[] { new ParamInfo("columns_breps", "columns_breps", "list", "Generic Data", false) });

        var merged = HintSelection.ApplyDeclaredHints(
            introspection,
            new[] { new IoParamSpec("in1", "list", "string"), new IoParamSpec("in2", "tree", "Curve") },
            new[] { new IoParamSpec("columns_breps", "list", "Brep") });

        Assert.Equal("string", merged.Inputs[0].Hint);  // filled from the declaration
        Assert.Equal("Line", merged.Inputs[1].Hint);    // the raw read wins when present
        Assert.Equal("Brep", merged.Outputs[0].Hint);
    }

    [Fact]
    public void Declared_hints_match_by_nickname_and_skip_unhinted_specs()
    {
        var introspection = new ComponentIntrospection(
            Guid.NewGuid(), "Script", "W1",
            new[] { new ParamInfo("Variable", "in1", "tree", "Generic Data", true) },
            Array.Empty<ParamInfo>());

        var merged = HintSelection.ApplyDeclaredHints(
            introspection, new[] { new IoParamSpec("in1", "tree") }, Array.Empty<IoParamSpec>());

        Assert.Equal("", merged.Inputs[0].Hint); // spec carries no hint — nothing to declare
    }
}
