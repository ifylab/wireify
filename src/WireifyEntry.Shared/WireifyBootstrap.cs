// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Reflection;
using WireifyContract;

namespace Wireify.Entry
{
    /// <summary>
    /// One-time bootstrap of the isolated core, shared as source by both entry assemblies. The only
    /// reflection in the plugin lives here: load WireifyCore into the <see cref="IsolatedLoadContext"/>,
    /// invoke <c>WireifyEntryPoint.CreateController()</c>, and hand back the result typed as
    /// <see cref="IWireifyController"/> — the cast works because WireifyContract is deferred to the
    /// Default context, so both sides see the same type identity. The
    /// <see cref="WireifyControllerLocator"/> (also in the Default-loaded contract) guarantees the
    /// <c>.rhp</c> and the <c>.gha</c> share one controller, whichever bootstraps first.
    /// </summary>
    internal static class WireifyBootstrap
    {
        // Held so the load context is never collected while Rhino runs.
        static IsolatedLoadContext? _alc;

        public static IWireifyController EnsureController()
            => WireifyControllerLocator.GetOrCreate(Create);

        static IWireifyController Create()
        {
            var anchor = typeof(WireifyBootstrap).Assembly.Location;
            var dir = Path.GetDirectoryName(anchor);
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException("Could not resolve the Wireify plugin folder.");

            var corePath = Path.Combine(dir!, "WireifyCore.dll");
            if (!File.Exists(corePath))
                throw new FileNotFoundException("WireifyCore.dll not found beside the plugin (packaging issue).", corePath);

            var alc = new IsolatedLoadContext(corePath, "WireifyContract");
            var coreAsm = alc.LoadFromAssemblyPath(corePath);
            var entry = coreAsm.GetType("WireifyCore.Hosting.WireifyEntryPoint", throwOnError: true)!;
            var create = entry.GetMethod("CreateController", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(entry.FullName, "CreateController");
            var controller = create.Invoke(null, null)
                ?? throw new InvalidOperationException("WireifyEntryPoint.CreateController returned null.");

            _alc = alc;
            return (IWireifyController)controller;
        }
    }
}
