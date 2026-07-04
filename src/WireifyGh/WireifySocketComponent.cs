// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using GH_IO.Serialization;
using Rhino;
using WireifyContract;

namespace WireifyGh
{
    /// <summary>
    /// The Wireify socket — the staging component of the agreed Connect-UI design. Merge-pattern
    /// variable inputs (wire and rename them to what the script should call them), no outputs, a
    /// no-op solve (Grasshopper still collects the input data, which is what lets Claude read live
    /// values before any code exists). Carries a canvas-visible number badge; Claude converts it
    /// in place into a stock Python 3 component via the convert_staged tool, so saved definitions
    /// never depend on Wireify.
    /// </summary>
    public sealed class WireifySocketComponent : GH_Component, IGH_VariableParameterComponent
    {
        static System.Drawing.Bitmap? _icon;

        int _number;

        public WireifySocketComponent() : base(
            "Wireify",
            "W?",
            "Stage inputs for a Python component Claude will write: wire and name the inputs, then "
            + "tell Claude in the terminal, e.g. \"do #3: cull panels below the area threshold\". "
            + "Claude reads the live input data and converts this socket, in place, into a normal "
            + "Python 3 script component - wires kept, no Wireify needed to open the file afterwards.",
            "Wireify",
            "Connect")
        {
        }

        public override Guid ComponentGuid => WireifyIds.SocketComponentGuid;

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap? Icon => _icon ??= LoadIcon();

        internal int Number => _number;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("in1", "in1", StagedInputDescription, GH_ParamAccess.tree);
            pManager.AddGenericParameter("in2", "in2", StagedInputDescription, GH_ParamAccess.tree);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // No outputs while staged — they appear on the converted Python component.
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            // Deliberately empty: the socket computes nothing, but its params collect VolatileData,
            // so read_input_data works before any code exists.
        }

        // --- numbering -------------------------------------------------------------------------

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            if (_number <= 0 || NumberTaken(document, _number))
                _number = NextFreeNumber(document);
            NickName = WireifyIds.MakeNickname(_number);
        }

        bool NumberTaken(GH_Document document, int number)
        {
            foreach (var other in NumbersInUse(document)) if (other == number) return true;
            return false;
        }

        int NextFreeNumber(GH_Document document)
        {
            var used = new HashSet<int>(NumbersInUse(document));
            var candidate = 1;
            while (used.Contains(candidate)) candidate++;
            return candidate;
        }

        IEnumerable<int> NumbersInUse(GH_Document document)
        {
            foreach (var obj in document.Objects)
            {
                if (ReferenceEquals(obj, this)) continue;
                if (obj is WireifySocketComponent socket && socket._number > 0)
                    yield return socket._number;
                else if (obj is IGH_Component comp && WireifyIds.TryParseNumber(comp.NickName, out var n))
                    yield return n;
            }
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("WireifyNumber", _number);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            reader.TryGetInt32("WireifyNumber", ref _number);
            return base.Read(reader);
        }

        // --- button ----------------------------------------------------------------------------

        internal string ButtonLabel
        {
            get
            {
                if (WireifyGhRuntime.IsActive(InstanceGuid)) return "Working";
                // Short enough to never clip in the capsule (live round: "Ready - do #1" truncated).
                return WireifyGhRuntime.State >= WireifyConnectionState.TerminalLaunched
                    ? $"do #{_number}"
                    : "Connect";
            }
        }

        internal void OnButtonClick()
        {
            if (WireifyGhRuntime.State >= WireifyConnectionState.TerminalLaunched)
            {
                TryOpenPanel();
                return;
            }
            LaunchConnect();
        }

        /// <summary>Run the Connect flow regardless of session state — a fresh terminal every time.
        /// This is how the user gets Claude back after closing the window (the plugin cannot see a
        /// terminal close, so the state may still read connected).</summary>
        internal void LaunchConnect()
        {
            var controller = WireifyGhRuntime.Controller;
            var doc = OnPingDocument();
            if (doc is null) return;
            if (string.IsNullOrEmpty(doc.FilePath))
            {
                Rhino.UI.Dialogs.ShowMessage(
                    "Save the definition first - Wireify keys the agent home to the .gh file path.",
                    "Wireify");
                return;
            }

            var path = doc.FilePath;
            TryOpenPanel();
            Task.Run(() =>
            {
                try
                {
                    var report = controller.Connect(path);
                    if (!report.Success && report.Hint is { Length: > 0 } hint)
                        RhinoApp.WriteLine($"[wireify] {hint}");
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[wireify] connect failed: {ex.Message}");
                }
            });
        }

        static void TryOpenPanel()
        {
            // Panel lives in Wireify.rhp; stay graceful when only the .gha is installed (dev).
            try { Rhino.UI.Panels.OpenPanel(WireifyIds.PanelGuid); } catch { }
        }

        public override void CreateAttributes() => m_attributes = new WireifySocketAttributes(this);

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            // Always offered: on platforms where the terminal window is untrackable (mac) the state
            // can stay green after a close, so relaunching must never be gated on state.
            Menu_AppendItem(menu, "Open Claude terminal", (_, _) => LaunchConnect());
            Menu_AppendItem(menu, "Open Wireify panel", (_, _) => TryOpenPanel());
        }

        // --- variable inputs (the Merge pattern) -----------------------------------------------

        public bool CanInsertParameter(GH_ParameterSide side, int index) => side == GH_ParameterSide.Input;

        public bool CanRemoveParameter(GH_ParameterSide side, int index)
            => side == GH_ParameterSide.Input && Params.Input.Count > 1;

        public IGH_Param CreateParameter(GH_ParameterSide side, int index)
            => new Param_GenericObject
            {
                Name = $"in{index + 1}",
                NickName = $"in{index + 1}",
                Description = StagedInputDescription,
                Access = GH_ParamAccess.tree,
                Optional = true,
            };

        public bool DestroyParameter(GH_ParameterSide side, int index) => true;

        public void VariableParameterMaintenance()
        {
            for (var i = 0; i < Params.Input.Count; i++)
            {
                var param = Params.Input[i];
                param.Optional = true;
                param.Access = GH_ParamAccess.tree;
                param.MutableNickName = true;
                if (string.IsNullOrEmpty(param.NickName))
                {
                    param.Name = $"in{i + 1}";
                    param.NickName = $"in{i + 1}";
                }
            }
        }

        const string StagedInputDescription =
            "Staged input - rename it to what the generated script should call it.";

        static System.Drawing.Bitmap? LoadIcon()
        {
            var stream = typeof(WireifySocketComponent).Assembly
                .GetManifestResourceStream("WireifyGh.Resources.wireify-24.png");
            return stream is null ? null : new System.Drawing.Bitmap(stream);
        }
    }
}
