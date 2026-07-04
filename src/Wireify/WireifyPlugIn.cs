// SPDX-License-Identifier: Apache-2.0
using System;
using Rhino.PlugIns;
using Rhino.UI;

namespace Wireify
{
    /// <summary>
    /// The Rhino-side entry: registers the Connect/Status panel and the <c>_Wireify</c> command.
    /// Deliberately does NOT start the MCP server here — the server needs Grasshopper, and
    /// <c>WireifyGh.gha</c> brings it up when GH loads; the panel and command also start it on
    /// demand. Loading at startup only registers UI.
    /// </summary>
    public sealed class WireifyPlugIn : PlugIn
    {
        public WireifyPlugIn() => Instance = this;

        public static WireifyPlugIn? Instance { get; private set; }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                Panels.RegisterPanel(this, typeof(WireifyPanel), "Wireify", PanelIcon(), PanelType.System);
                return LoadReturnCode.Success;
            }
            catch (Exception ex)
            {
                errorMessage = $"Wireify panel registration failed: {ex.Message}";
                return LoadReturnCode.ErrorShowDialog;
            }
        }

        static System.Drawing.Icon PanelIcon()
        {
            var stream = typeof(WireifyPlugIn).Assembly.GetManifestResourceStream("Wireify.Resources.wireify.ico")
                ?? throw new InvalidOperationException("wireify.ico missing from embedded resources.");
            using (stream) return new System.Drawing.Icon(stream);
        }
    }
}
