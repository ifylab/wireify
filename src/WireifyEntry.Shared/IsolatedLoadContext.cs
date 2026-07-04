// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Wireify.Entry
{
    /// <summary>
    /// Loads WireifyCore + its out-of-band dependencies (System.Text.Json 10.x, the MCP SDK, the
    /// Extensions.AI libs, ...) from our own folder, so the framework's older in-box System.Text.Json
    /// (already loaded in the Default context) never shadows them. Assemblies named in
    /// <c>sharedAssemblyNames</c> — the WireifyContract seam — plus anything not in our folder
    /// (framework runtime, RhinoCommon, Grasshopper) defer to the Default context, keeping one type
    /// identity across the boundary. This is the pattern the Validation Gate proved; shared as source
    /// by both entry assemblies (<c>Wireify.rhp</c> and <c>WireifyGh.gha</c>).
    /// </summary>
    internal sealed class IsolatedLoadContext : AssemblyLoadContext
    {
        readonly AssemblyDependencyResolver _resolver;
        readonly string _dir;
        readonly string[] _sharedAssemblyNames;

        public IsolatedLoadContext(string mainAssemblyPath, params string[] sharedAssemblyNames)
            : base("wireify", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _dir = Path.GetDirectoryName(mainAssemblyPath)!;
            _sharedAssemblyNames = sharedAssemblyNames ?? Array.Empty<string>();
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not { } simpleName) return null;

            // The contract must resolve to the Default context's copy (the entry assembly already
            // loaded it), even though a physical copy sits in our folder too.
            foreach (var shared in _sharedAssemblyNames)
                if (string.Equals(simpleName, shared, StringComparison.OrdinalIgnoreCase))
                    return null;

            var resolved = _resolver.ResolveAssemblyToPath(name);
            if (resolved != null) return LoadFromAssemblyPath(resolved);

            var local = Path.Combine(_dir, simpleName + ".dll");
            if (File.Exists(local)) return LoadFromAssemblyPath(local);

            return null; // defer to the Default context: framework runtime + RhinoCommon/Grasshopper
        }
    }
}
