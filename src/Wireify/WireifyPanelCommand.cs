// SPDX-License-Identifier: Apache-2.0
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using WireifyContract;

namespace Wireify
{
    /// <summary>
    /// <c>_WireifyPanel</c> — bring the panel back without a Build. <c>_Wireify</c> also runs
    /// Connect, which opens another Claude terminal when the user only closed the panel.
    /// </summary>
    public sealed class WireifyPanelCommand : Command
    {
        public override string EnglishName => "WireifyPanel";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(WireifyIds.PanelGuid);
            return Result.Success;
        }
    }
}
