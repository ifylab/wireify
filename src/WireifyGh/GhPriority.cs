// SPDX-License-Identifier: Apache-2.0
using System;
using Grasshopper.Kernel;
using Rhino;

namespace WireifyGh
{
    /// <summary>
    /// Runs once when Grasshopper loads this .gha (in the Default load context): bootstrap the
    /// isolated core through the typed contract seam and bring the loopback MCP server up, so a
    /// dropped socket or a Connect always finds it listening. No model calls, no token reads;
    /// loopback only.
    /// </summary>
    public sealed class GhPriority : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            RegisterRibbonIcon();
            WireifyBadgeOverlay.Install();
            try
            {
                var controller = WireifyGhRuntime.Controller;
                var info = controller.EnsureServer();
                RhinoApp.WriteLine($"[wireify] MCP server listening on {info.Url}");
                RhinoApp.WriteLine("[wireify] drop a Wireify component (or run _Wireify) to connect Claude.");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[wireify] FAILED to start: {ex.Message}");
            }
            return GH_LoadingInstruction.Proceed;
        }

        static void RegisterRibbonIcon()
        {
            try
            {
                var stream = typeof(GhPriority).Assembly
                    .GetManifestResourceStream("WireifyGh.Resources.wireify-16.png");
                if (stream is null) return;
                using (stream)
                {
                    Grasshopper.Instances.ComponentServer.AddCategoryIcon("Wireify", new System.Drawing.Bitmap(stream));
                    Grasshopper.Instances.ComponentServer.AddCategorySymbolName("Wireify", 'W');
                }
            }
            catch { /* a missing ribbon icon must never block the plugin */ }
        }
    }
}
