// SPDX-License-Identifier: Apache-2.0
using System;
using System.Threading.Tasks;
using Grasshopper.Kernel;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Reflection shims for the RhinoCode script-component API. The concrete types
    /// (<c>RhinoCodePluginGH.Components.Python3Component</c>, the <c>IScriptObject</c> interface)
    /// live in <c>RhinoCodePluginGH</c>, which is not on NuGet and not semver — so per the build
    /// plan we touch it only by reflection: official members where they exist, fail-soft otherwise.
    /// Method names are the ones the spikes confirmed on Rhino 8 SR18.
    /// </summary>
    internal static class RhinoCodeInterop
    {
        /// <summary>Set the executable script source (CPython 3 path).</summary>
        public static void SetSource(IGH_DocumentObject component, string source)
        {
            var mi = component.GetType().GetMethod("SetSource", new[] { typeof(string) });
            if (mi is null)
                throw new MissingMethodException(component.GetType().FullName, "SetSource(string)");
            mi.Invoke(component, new object[] { source });
        }

        /// <summary>Set the writable <c>Code</c> property on the legacy GhPython (IronPython 2) component.</summary>
        public static void SetIronPythonCode(IGH_DocumentObject component, string code)
        {
            var prop = component.GetType().GetProperty("Code");
            if (prop is null || !prop.CanWrite)
                throw new MissingMemberException(component.GetType().FullName, "Code");
            prop.SetValue(component, code, null);
        }

        /// <summary>
        /// Read a script component's current source: Rhino 8 components expose
        /// <c>TryGetSource(out string)</c> (spike-validated — it returns the executable code, where
        /// <c>Text</c> is display-only); legacy GhPython exposes the <c>Code</c> property.
        /// </summary>
        public static bool TryGetSource(IGH_DocumentObject component, out string source)
        {
            source = "";

            foreach (var mi in component.GetType().GetMethods())
            {
                if (mi.Name != "TryGetSource") continue;
                var ps = mi.GetParameters();
                if (ps.Length != 1 || !ps[0].IsOut || ps[0].ParameterType.GetElementType() != typeof(string)) continue;
                var args = new object?[] { null };
                if (mi.Invoke(component, args) is bool ok && ok && args[0] is string s)
                {
                    source = s;
                    return true;
                }
                return false;
            }

            var code = component.GetType().GetProperty("Code");
            if (code is { CanRead: true } && code.GetValue(component, null) is string legacy)
            {
                source = legacy;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Recompile after a source change. <c>ReBuild()</c> is an explicit-interface async method
        /// on <c>IScriptObject</c>; we cast, invoke, and wait on the returned Task. Required — set
        /// source alone does not compile.
        /// </summary>
        public static void ReBuild(IGH_DocumentObject component, TimeSpan timeout)
        {
            foreach (var itf in component.GetType().GetInterfaces())
            {
                if (itf.Name != "IScriptObject") continue;
                var mi = itf.GetMethod("ReBuild", Type.EmptyTypes);
                if (mi is null) continue;
                var result = mi.Invoke(component, null);
                if (result is Task task) task.Wait(timeout);
                return;
            }
            // No IScriptObject -> nothing to recompile (non-script component); fail soft.
        }

        /// <summary>Sync component params from an SDK-mode <c>RunScript</c> signature. NB this is
        /// an SDK-mode facility — plain script-mode components derive nothing for inputs (McNeel
        /// discourse 199692; live-confirmed). Fail-soft if absent.</summary>
        public static void SetParametersFromScript(IGH_DocumentObject component)
        {
            var mi = component.GetType().GetMethod("SetParametersFromScript", Type.EmptyTypes);
            mi?.Invoke(component, null);
        }

        /// <summary>
        /// Construct a <c>RhinoCodePluginGH.Parameters.ScriptVariableParam</c> — the sanctioned way
        /// to give a script component named variables (the same object the component's own ZUI
        /// creates). Ctor + cosmetic members by reflection (unversioned assembly); everything typed
        /// (<c>Access</c>/<c>Optional</c>/<c>CreateAttributes</c>) through the public GH surface.
        /// The exact member set is pinned live by spike_8.
        /// </summary>
        public static IGH_Param CreateScriptVariableParam(
            IGH_DocumentObject scriptComponent, string name, GH_ParamAccess access, bool optional, string? typeHint)
        {
            var asm = scriptComponent.GetType().Assembly;
            var type = asm.GetType("RhinoCodePluginGH.Parameters.ScriptVariableParam", throwOnError: false)
                ?? throw new MissingMemberException(
                    "RhinoCodePluginGH.Parameters.ScriptVariableParam not found — Rhino 8 SR18+ required.");

            var ctor = type.GetConstructor(new[] { typeof(string) })
                ?? throw new MissingMethodException(type.FullName, ".ctor(string)");
            var param = (IGH_Param)ctor.Invoke(new object[] { name });

            param.Name = name;
            param.NickName = name;
            param.Access = access;
            param.Optional = optional;

            TrySetProperty(param, "PrettyName", name);
            TrySetProperty(param, "AllowTreeAccess", true);
            if (!string.IsNullOrWhiteSpace(typeHint)) TrySelectTypeHint(param, typeHint!);

            param.CreateAttributes();
            return param;
        }

        /// <summary>Re-sync a script component after param changes (required per the official
        /// recipe). Typed via <see cref="IGH_VariableParameterComponent"/>; reflection fallback.</summary>
        public static void VariableParameterMaintenance(IGH_DocumentObject component)
        {
            if (component is IGH_VariableParameterComponent vpc)
            {
                vpc.VariableParameterMaintenance();
                return;
            }
            var mi = component.GetType().GetMethod("VariableParameterMaintenance", Type.EmptyTypes);
            mi?.Invoke(component, null);
        }

        static void TrySetProperty(object target, string property, object value)
        {
            try
            {
                var prop = target.GetType().GetProperty(property);
                if (prop is { CanWrite: true }) prop.SetValue(target, value, null);
            }
            catch { /* cosmetic member — never fail the build over it */ }
        }

        static void TrySelectTypeHint(object param, string hint)
        {
            try
            {
                var hintsProp = param.GetType().GetProperty("TypeHints");
                var hints = hintsProp?.GetValue(param, null);
                if (hints is null) return;
                var select = hints.GetType().GetMethod("Select", new[] { typeof(string) });
                select?.Invoke(hints, new object[] { hint });
            }
            catch { /* hints are sugar — generic params still work */ }
        }
    }
}
