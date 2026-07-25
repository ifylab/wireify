// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Pure hint-picking rules for script variable params — kept free of Rhino types so they are
    /// unit-testable. The hint registry offers only CONCRETE types (bool ... Leader; there is no
    /// dynamic/object entry, live-confirmed), and a hintless param marshals geometry to the script
    /// as script-doc Guid references — so when the caller omits a hint, the hint is auto-selected
    /// from the live wired data instead: one mappable CLR type picks its token, a mixed tree picks
    /// nothing and warns.
    /// </summary>
    public static class HintSelection
    {
        /// <summary>Resolve a hint token against the registry's names, case-insensitively,
        /// returning the registry's own spelling — or null when absent.</summary>
        public static string? Resolve(string token, IReadOnlyList<string> available)
            => available.FirstOrDefault(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));

        /// <summary>Resolve an explicitly requested hint. An empty available list means the
        /// registry could not be enumerated — trust the request as-is. A non-empty list without a
        /// match throws, naming what exists, so a typo fails loudly instead of silently leaving
        /// the param generic.</summary>
        public static string PickExplicit(string requested, IReadOnlyList<string> available)
        {
            if (string.IsNullOrWhiteSpace(requested))
                throw new ArgumentException("type hint name is empty.", nameof(requested));
            if (available.Count == 0) return requested;

            return Resolve(requested, available) ?? throw new ArgumentException(
                $"type hint '{requested}' is not available on this component. " +
                $"Available hints: {string.Join(", ", available)}.");
        }

        /// <summary>The hint token for a CLR type name, or null when nothing safe maps. Curve and
        /// surface subclasses fold to their base tokens; integral types beyond Int32 stay unmapped
        /// (a lossy conversion must be the caller's explicit choice).</summary>
        public static string? AutoHint(string clrTypeName)
        {
            if (string.IsNullOrWhiteSpace(clrTypeName)) return null;
            if (ClrToHint.TryGetValue(clrTypeName, out var token)) return token;
            if (CurveClrNames.Contains(clrTypeName)) return "Curve";
            if (SurfaceClrNames.Contains(clrTypeName)) return "Surface";
            return null;
        }

        /// <summary>Fill introspection hint fields the raw read left empty from the specs just
        /// declared — the reflection read of the selected hint is best-effort against a private
        /// registry shape, but the caller of set_io KNOWS what it selected, and the solve proves
        /// it applies; the echo should say so.</summary>
        public static ComponentIntrospection ApplyDeclaredHints(
            ComponentIntrospection introspection,
            IReadOnlyList<IoParamSpec> inputs,
            IReadOnlyList<IoParamSpec> outputs)
        {
            return introspection with
            {
                Inputs = Merge(introspection.Inputs, inputs),
                Outputs = Merge(introspection.Outputs, outputs),
            };

            static IReadOnlyList<ParamInfo> Merge(IReadOnlyList<ParamInfo> infos, IReadOnlyList<IoParamSpec> specs)
                => infos.Select(info =>
                {
                    if (info.Hint.Length > 0) return info;
                    var spec = specs.FirstOrDefault(s =>
                        string.Equals(s.Name, info.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s.Name, info.NickName, StringComparison.OrdinalIgnoreCase));
                    return spec is { TypeHint: { Length: > 0 } hint } ? info with { Hint = hint } : info;
                }).ToList();
        }

        /// <summary>The user-facing mixed-tree message: no single hint fits, geometry items reach
        /// the script as script-doc Guid references, and the two real ways out are named.</summary>
        public static string MixedTreeWarning(string param, IReadOnlyCollection<string> clrTypeNames)
            => $"'{param}' mixes {string.Join(" and ", clrTypeNames)} across its branches — no single type hint fits, " +
               "so geometry items reach the script as script-doc Guid references (strings and numbers pass through). " +
               "For native types, wire each type into its own staged input and hint each; to keep this wiring, " +
               "dereference the Guids in-script via scriptcontext (sc.doc.Objects.FindId(g) / rs.coerceline(g)).";

        static readonly Dictionary<string, string> ClrToHint = new(StringComparer.Ordinal)
        {
            ["System.String"] = "string",
            ["System.Double"] = "double",
            ["System.Single"] = "double",
            ["System.Int32"] = "int",
            ["System.Boolean"] = "bool",
            ["System.Guid"] = "Guid",
            ["System.DateTime"] = "DateTime",
            ["System.Drawing.Color"] = "Color",
            ["Rhino.Geometry.Point3d"] = "Point3d",
            ["Rhino.Geometry.Vector3d"] = "Vector3d",
            ["Rhino.Geometry.Plane"] = "Plane",
            ["Rhino.Geometry.Interval"] = "Interval",
            ["Rhino.Geometry.Line"] = "Line",
            ["Rhino.Geometry.Circle"] = "Circle",
            ["Rhino.Geometry.Arc"] = "Arc",
            ["Rhino.Geometry.Polyline"] = "Polyline",
            ["Rhino.Geometry.Box"] = "Box",
            ["Rhino.Geometry.Transform"] = "Transform",
            ["Rhino.Geometry.Rectangle3d"] = "Rectangle3d",
            ["Rhino.Geometry.Mesh"] = "Mesh",
            ["Rhino.Geometry.Brep"] = "Brep",
            ["Rhino.Geometry.SubD"] = "SubD",
            ["Rhino.Geometry.Extrusion"] = "Extrusion",
            ["Rhino.Geometry.PointCloud"] = "PointCloud",
            ["Rhino.Geometry.GeometryBase"] = "GeometryBase",
        };

        static readonly HashSet<string> CurveClrNames = new(StringComparer.Ordinal)
        {
            "Rhino.Geometry.Curve",
            "Rhino.Geometry.LineCurve",
            "Rhino.Geometry.ArcCurve",
            "Rhino.Geometry.NurbsCurve",
            "Rhino.Geometry.PolyCurve",
            "Rhino.Geometry.PolylineCurve",
        };

        static readonly HashSet<string> SurfaceClrNames = new(StringComparer.Ordinal)
        {
            "Rhino.Geometry.Surface",
            "Rhino.Geometry.NurbsSurface",
            "Rhino.Geometry.PlaneSurface",
            "Rhino.Geometry.RevSurface",
            "Rhino.Geometry.SumSurface",
        };
    }
}
