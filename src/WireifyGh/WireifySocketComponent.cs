// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
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
            // The socket computes nothing (its params collect VolatileData, so read_input_data
            // works before any code exists) — but staging is the one moment Wireify can warn the
            // USER about clipped text, so scan a bounded prefix of each wired input for it.
            SettleNumber();
            WarnOnClippedText();
        }

        /// <summary>Two objects must never carry one number — "do #n" is resolved by number
        /// client-side, so a duplicate can convert the wrong socket. A number is settled at
        /// SOLVE time, after whatever transaction added the socket has completed: the first
        /// holder in document order keeps it, a later one (a paste, round-12 S12.13) takes the
        /// next free number. A socket re-added by undoing its own conversion solves after the
        /// converted component is gone, so it keeps its number (round-11 B85). The earlier
        /// "re-check on the next idle beat" never ran on the paste path.</summary>
        void SettleNumber()
        {
            var doc = OnPingDocument();
            if (doc is null || _number <= 0) return;
            foreach (var obj in doc.Objects)
            {
                if (ReferenceEquals(obj, this)) return; // the first holder keeps it
                var other = obj is WireifySocketComponent socket ? socket._number
                    : obj is IGH_Component comp && WireifyIds.TryParseNumber(comp.NickName, out var n) ? n
                    : 0;
                if (other != _number) continue;
                _number = NextFreeNumber(doc);
                NickName = WireifyIds.MakeNickname(_number);
                Repaint();
                return;
            }
        }

        /// <summary>Orange-balloon warning when a staged input carries text at exactly the panel
        /// paste-clip length — the full content never reached the wire, and downstream that only
        /// surfaces as a parse error deep inside the truncated data. Bounded scan: string items
        /// only, first few per branch.</summary>
        void WarnOnClippedText()
        {
            const int maxItemsPerBranch = 8;
            foreach (var param in Params.Input)
            {
                if (param.SourceCount == 0) continue;
                var clipped = 0;
                foreach (var path in param.VolatileData.Paths)
                {
                    var seen = 0;
                    foreach (var item in param.VolatileData.get_Branch(path))
                    {
                        if (seen++ >= maxItemsPerBranch) break;
                        if (item is Grasshopper.Kernel.Types.GH_String text
                            && text.Value is { } value
                            && value.Length == WireifyIds.PanelClipTextLength)
                            clipped++;
                    }
                }
                if (clipped > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        WireifyIds.ClipTextWarning(param.NickName ?? param.Name ?? "input", clipped));
            }
        }

        // --- numbering -------------------------------------------------------------------------

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            // A persisted number is kept as it is: a socket re-added by an undo arrives while the
            // converted W<n> component still holds the number for a moment (round-11 S11.39),
            // and a paste arrives beside the socket it was copied from (round-12 S12.13). Both
            // settle at the next solve (SettleNumber), after the transaction that added them.
            if (_number <= 0)
                _number = NextFreeNumber(document);
            NickName = WireifyIds.MakeNickname(_number);
            document.FilePathChanged -= OnFilePathChanged;
            document.FilePathChanged += OnFilePathChanged;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            document.FilePathChanged -= OnFilePathChanged;
            base.RemovedFromDocument(document);
        }

        /// <summary>Save As splits "the definition" in two: the home (and any app page) stays with
        /// the OLD path, the live session follows this document instance to the new one. The
        /// plate tracked the split silently before (round-9 S9.13); now it says so, and the
        /// controller moves the session so both files read honestly (Session open here, Build
        /// on the old path). A first save (no old path) just refreshes the plate.</summary>
        void OnFilePathChanged(object sender, GH_DocFilePathEventArgs e)
        {
            var oldPath = e.OldFilePath ?? "";
            var newPath = e.NewFilePath ?? "";
            WireifyGhRuntime.DefinitionRenamed(oldPath, newPath);
            if (oldPath.Length > 0 && newPath.Length > 0
                && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
                && WireifyGhRuntime.AppStatusFor(oldPath).PageExists)
            {
                Flash($"saved under a new name — the app stays with {Path.GetFileName(oldPath)}; Build here starts a new one",
                    warm: true, ms: 8000);
                return;
            }
            Repaint();
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

        // --- the plate: Build / Open app + the message lines ------------------------------------

        // Build-flow residue shown on the plate: the running step while the flow runs, the
        // failure + hint after a failed run (until the next Build click), and a short-lived
        // line for clicks ("link copied"). Live state only — never written into the file.
        bool _building;
        string _buildError = "";
        string _buildHint = "";
        string _transient = "";
        bool _transientWarm;
        DateTime _transientUntil;

        string? DocPath => OnPingDocument()?.FilePath;

        /// <summary>The plate's copy for this paint, composed from live state (per document:
        /// THIS definition's session decides — a fresh second file honestly reads Build instead
        /// of inheriting another file's live terminal).</summary>
        internal SocketText Text
        {
            get
            {
                var path = DocPath;
                var names = new string[Params.Input.Count];
                var wired = new bool[Params.Input.Count];
                for (var i = 0; i < Params.Input.Count; i++)
                {
                    names[i] = Params.Input[i].NickName ?? "";
                    wired[i] = Params.Input[i].SourceCount > 0;
                }
                return SocketCopy.Compose(new SocketView
                {
                    Number = _number,
                    InputNames = names,
                    InputWired = wired,
                    SessionOpen = WireifyGhRuntime.StateFor(path) >= WireifyConnectionState.TerminalLaunched,
                    Building = _building,
                    BuildStep = _building ? StepPhrase(WireifyGhRuntime.LastStep) : "",
                    BuildError = _buildError,
                    BuildHint = _buildHint,
                    App = WireifyGhRuntime.AppStatusFor(path),
                    Transient = DateTime.Now < _transientUntil ? _transient : "",
                    TransientWarm = _transientWarm,
                });
            }
        }

        static string StepPhrase(WireifyConnectStep? step)
        {
            if (step is null) return "";
            switch (step.Kind)
            {
                case "server": return "starting the local server";
                case "home": return "preparing this file's home";
                case "config": return "writing the MCP config";
                case "preflight": return "checking Claude Code";
                case "terminal": return "opening the terminal";
                default: return StripScope(step.Message);
            }
        }

        internal void OnBuildClick()
        {
            if (_building) return;
            if (WireifyGhRuntime.StateFor(DocPath) >= WireifyConnectionState.TerminalLaunched)
            {
                // Inert by design (Hossein's call): a live session means "use its terminal" — a
                // second terminal is a deliberate act, one right-click away.
                Flash("session open — use its terminal (right-click: New Claude session)", warm: false);
                return;
            }
            LaunchConnect();
        }

        internal void OnAppClick()
        {
            var path = DocPath;
            var status = WireifyGhRuntime.AppStatusFor(path);
            if (!status.PageExists)
            {
                Flash(string.IsNullOrEmpty(path)
                    ? "save the definition first — Wireify keys its home to the file path"
                    : "no app page yet — Build, then ask Claude: make me an app page", warm: true);
                return;
            }
            var result = WireifyGhRuntime.OpenApp(path);
            Flash(result.Ok ? "opening the app in your browser" : result.Message, warm: !result.Ok);
        }

        internal void OnAddressClick()
        {
            var link = WireifyGhRuntime.AppLinkFor(DocPath);
            if (link is null)
            {
                Flash("no app page yet", warm: true);
                return;
            }
            try
            {
                Clipboard.SetText(link);
                Flash("link copied", warm: false);
            }
            catch
            {
                Flash("could not copy — the link is in the Wireify panel log after Build", warm: true);
            }
        }

        internal void OpenAppFolder()
        {
            var status = WireifyGhRuntime.AppStatusFor(DocPath);
            if (string.IsNullOrEmpty(status.AppFolder))
            {
                Flash("no app folder yet — Build, then ask Claude: make me an app page", warm: true);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(status.AppFolder) { UseShellExecute = true });
            }
            catch { /* opening a folder is never worth an error dialog */ }
        }

        void Flash(string text, bool warm, int ms = 2500)
        {
            _transient = text;
            _transientWarm = warm;
            _transientUntil = DateTime.Now.AddMilliseconds(ms);
            Repaint();
            WireifyGhRuntime.RepaintAfter(ms + 100);
        }

        void Repaint()
        {
            Attributes?.ExpireLayout();
            try { Grasshopper.Instances.RedrawCanvas(); } catch { /* best-effort */ }
        }

        /// <summary>Run the build flow (home, config, preflight, terminal) regardless of session
        /// state — a fresh terminal every time. This is how the user gets Claude back after
        /// closing the window (the plugin cannot see a terminal close on every platform, so the
        /// state may still read open); the plate reports each step and any failure inline.</summary>
        internal void LaunchConnect()
        {
            var controller = WireifyGhRuntime.Controller;
            var doc = OnPingDocument();
            if (doc is null) return;
            if (string.IsNullOrEmpty(doc.FilePath))
            {
                Flash("save the definition first — Wireify keys its home to the file path", warm: true, ms: 4000);
                return;
            }

            var path = doc.FilePath;
            _building = true;
            _buildError = "";
            _buildHint = "";
            Repaint();
            TryOpenPanel();
            Task.Run(() =>
            {
                string error = "", hint = "";
                try
                {
                    var report = controller.Connect(path);
                    if (!report.Success)
                    {
                        WireifyConnectStep? failed = null;
                        foreach (var step in report.Steps)
                            if (!step.Ok) failed = step;
                        error = failed is null ? "build failed" : StripScope(failed.Message);
                        hint = report.Hint ?? "";
                        if (hint.Length > 0) RhinoApp.WriteLine($"[wireify] {hint}");
                    }
                }
                catch (Exception ex)
                {
                    error = "build failed";
                    hint = ex.Message;
                    RhinoApp.WriteLine($"[wireify] connect failed: {ex.Message}");
                }
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    _building = false;
                    _buildError = error;
                    _buildHint = hint;
                    Repaint();
                }));
            });
        }

        static string StripScope(string? message)
        {
            var m = message ?? "";
            var close = m.StartsWith("[", StringComparison.Ordinal) ? m.IndexOf(']') : -1;
            return close > 0 ? m.Substring(close + 1).Trim() : m;
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
            Menu_AppendItem(menu, "New Claude session", (_, _) => LaunchConnect());
            Menu_AppendItem(menu, "Open companion app", (_, _) => OnAppClick());
            Menu_AppendItem(menu, "Copy app link", (_, _) => OnAddressClick());
            Menu_AppendItem(menu, "Open app folder", (_, _) => OpenAppFolder());
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
