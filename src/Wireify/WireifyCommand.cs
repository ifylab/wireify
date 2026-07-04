// SPDX-License-Identifier: Apache-2.0
using System;
using System.Threading.Tasks;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using Wireify.Entry;
using WireifyContract;

namespace Wireify
{
    /// <summary>
    /// <c>_Wireify</c> — the one Rhino action: open the panel and run Connect against the active
    /// definition. The connect steps stream into the panel; failures also echo to the command line.
    /// </summary>
    public sealed class WireifyCommand : Command
    {
        public override string EnglishName => "Wireify";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(WireifyIds.PanelGuid);

            var controller = WireifyBootstrap.EnsureController();
            Task.Run(() =>
            {
                try
                {
                    var report = controller.Connect(null);
                    if (!report.Success && report.Hint is { Length: > 0 } hint)
                        RhinoApp.WriteLine($"[wireify] {hint}");
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[wireify] connect failed: {ex.Message}");
                }
            });

            return Result.Success;
        }
    }
}
