// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Pure parsing of an SDK-mode script's <c>RunScript</c> signature. The RhinoCode engine
    /// treats the signature as derived state: on rebuild it rewrites the <c>def RunScript</c>
    /// line to match the component's CURRENT params, so source whose signature disagrees with
    /// the params comes back rewritten with its body referencing names that no longer exist —
    /// silently. The bridge uses this parse to refuse that write up front with the recipe
    /// (declare params first, then set the source) instead of letting the engine mangle it.
    /// </summary>
    public static class ScriptSignature
    {
        static readonly Regex RunScript = new(
            @"^\s*def\s+RunScript\s*\(([^)]*)\)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Extract the parameter names of the source's <c>RunScript</c> method, minus
        /// <c>self</c>, stripped of type annotations and defaults. Returns false when the source
        /// has no RunScript (plain script-mode code — nothing to check) or uses varargs (the
        /// engine's own generality — do not second-guess it).</summary>
        public static bool TryGetParams(string source, out IReadOnlyList<string> names)
        {
            names = Array.Empty<string>();
            var match = RunScript.Match(source ?? "");
            if (!match.Success) return false;

            var result = new List<string>();
            foreach (var raw in match.Groups[1].Value.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (part.StartsWith("*", StringComparison.Ordinal)) return false;
                var colon = part.IndexOf(':');
                if (colon >= 0) part = part.Substring(0, colon).Trim();
                var eq = part.IndexOf('=');
                if (eq >= 0) part = part.Substring(0, eq).Trim();
                if (part.Length == 0 || string.Equals(part, "self", StringComparison.Ordinal)) continue;
                result.Add(part);
            }
            names = result;
            return true;
        }
    }
}
