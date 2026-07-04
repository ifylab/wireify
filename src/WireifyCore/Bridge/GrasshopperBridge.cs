// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Rhino;
using WireifyContract;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// First-cut <see cref="IGrasshopperBridge"/> over the spike-validated recipe. Static
    /// Grasshopper APIs do the structural work (emit, add, wire, solve, read VolatileData and
    /// runtime messages); the RhinoCode-specific calls go through <see cref="RhinoCodeInterop"/>.
    /// The target document is resolved per call from a provider (the live active canvas in the
    /// plugin, a fixed in-memory doc in headless tests). Compile-checked on every platform; the
    /// runtime path is exercised inside Rhino.
    /// </summary>
    public sealed class GrasshopperBridge : IGrasshopperBridge
    {
        // Component server proxy GUIDs (confirmed by the spikes on Rhino 8 SR18).
        static readonly Guid CPython3Guid = new("719467e6-7cf5-4848-99b0-c5dd57e5442c");
        static readonly Guid IronPython2Guid = new("410755b1-224a-4c1e-a407-bf32fb45ea7e");

        const string Python3Directive = "#! python 3";

        readonly Func<GH_Document?> _activeDocument;
        readonly TimeSpan _rebuildTimeout;

        public GrasshopperBridge(Func<GH_Document?> activeDocument, TimeSpan? rebuildTimeout = null)
        {
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _rebuildTimeout = rebuildTimeout ?? TimeSpan.FromSeconds(30);
        }

        GH_Document Doc() => _activeDocument()
            ?? throw new InvalidOperationException("No active Grasshopper document — open a definition first.");

        // --- Orientation -------------------------------------------------------------------

        public DocumentSummary GetDocumentSummary(bool includeStagedData = false)
        {
            var doc = Doc();
            var components = doc.Objects
                .Select(o => new ComponentRef(o.InstanceGuid, o.Name ?? "", o.NickName ?? ""))
                .ToList();

            // The Wireify registry, derived from the document itself: sockets by ComponentGuid,
            // converted components by the W<n> nickname convention.
            var wireify = new List<WireifyComponentInfo>();
            foreach (var obj in doc.Objects)
            {
                if (obj is not IGH_Component comp) continue;
                if (comp.ComponentGuid == WireifyIds.SocketComponentGuid)
                {
                    WireifyIds.TryParseNumber(comp.NickName, out var n);
                    // Wired staged inputs can carry their live data (default caps) so a socket
                    // task orients in one call instead of one read per input.
                    var stagedData = includeStagedData
                        ? comp.Params.Input.Where(p => p.Sources.Count > 0)
                            .Select(p => ShapeParamData(p, ParamKey(p), 5, 50)).ToList()
                        : null;
                    wireify.Add(new WireifyComponentInfo(
                        n, comp.InstanceGuid, comp.NickName ?? "", "staged",
                        comp.Params.Input.Select(p => p.NickName ?? "").ToList(),
                        stagedData));
                }
                else if (WireifyIds.TryParseNumber(comp.NickName, out var n2))
                {
                    wireify.Add(new WireifyComponentInfo(
                        n2, comp.InstanceGuid, comp.NickName ?? "", "converted",
                        comp.Params.Input.Select(p => p.NickName ?? "").ToList()));
                }
            }

            return new DocumentSummary(
                string.IsNullOrEmpty(doc.FilePath) ? null : doc.FilePath,
                components,
                wireify.OrderBy(w => w.Number).ToList());
        }

        public ComponentIntrospection IntrospectComponent(Guid id) => Introspect(AsComponent(Find(Doc(), id)));

        public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
        {
            return Doc().Objects
                .Where(o => o.Attributes is { Selected: true })
                .OfType<IGH_Component>()
                .Select(Introspect)
                .ToList();
        }

        public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50)
        {
            var comp = AsComponent(Find(Doc(), id));
            var param = comp.Params.Input.FirstOrDefault(p => p.Name == inputParam || p.NickName == inputParam)
                ?? throw new ArgumentException($"No input '{inputParam}' on component {id}.", nameof(inputParam));
            return ShapeParamData(param, param.Name ?? inputParam, maxPerBranch, maxTotal);
        }

        InputData ShapeParamData(IGH_Param param, string reportName, int maxPerBranch, int maxTotal)
        {
            // Read VolatileData into a plain representation, then let the pure shaper do the
            // histogram / sampling / tree stats (so that logic is unit-tested without Rhino).
            // This runs on the UI thread, so per-item work is budgeted: the expensive calls
            // (ScriptVariable for the CLR type, ToString for the value) happen once per distinct
            // TypeName and only for items the shaper will actually sample; everything else is a
            // cheap TypeName read. Large wires must never pin Rhino.
            var volatileData = param.VolatileData;
            var branches = new List<ShapedBranch>();
            var clrByTypeName = new Dictionary<string, string>(StringComparer.Ordinal);
            var sampledTotal = 0;
            foreach (var path in volatileData.Paths)
            {
                var items = new List<ShapedItem>();
                var sampledInBranch = 0;
                foreach (var item in volatileData.get_Branch(path))
                {
                    if (item is null) continue;
                    var goo = item as IGH_Goo;
                    var typeName = goo?.TypeName ?? item.GetType().Name;
                    if (!clrByTypeName.TryGetValue(typeName, out var clr))
                    {
                        clr = ClrTypeOf(item);
                        clrByTypeName[typeName] = clr;
                    }

                    // Mirrors InputDataShaper's selection (first N per branch, first M overall, in
                    // order), so a value string exists exactly where a sample will be taken.
                    var value = "";
                    if (sampledInBranch < maxPerBranch && sampledTotal < maxTotal)
                    {
                        value = goo?.ToString() ?? item.ToString() ?? "";
                        sampledInBranch++;
                        sampledTotal++;
                    }
                    items.Add(new ShapedItem(typeName, clr, value));
                }
                branches.Add(new ShapedBranch(path.ToString(), items));
            }

            return InputDataShaper.Shape(reportName, AccessOf(param), branches, maxPerBranch, maxTotal);
        }

        public RuntimeInfo GetRuntimeInfo()
        {
            var version = RhinoApp.Version?.ToString() ?? "unknown";
            var runtimes = new List<string>();
            if (HasProxy(CPython3Guid)) runtimes.Add("cpython3");
            if (HasProxy(IronPython2Guid)) runtimes.Add("ironpython2");
            return new RuntimeInfo(version, runtimes, "unknown", RhinoCodeAssembliesLoaded());
        }

        static bool RhinoCodeAssembliesLoaded()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = asm.GetName().Name;
                    if (name != null && name.StartsWith("RhinoCodePluginGH", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* diagnostics only */ }
            return false;
        }

        public ScriptSource GetSource(Guid id)
        {
            var obj = Find(Doc(), id);
            if (!RhinoCodeInterop.TryGetSource(obj, out var source))
                throw new InvalidOperationException(
                    $"Component {id} ('{obj.Name}') does not expose readable script source — not a script component.");
            return new ScriptSource(id, obj.NickName ?? "", source);
        }

        // --- Build -------------------------------------------------------------------------

        public Guid CreatePythonComponent(PythonRuntime runtime) => CreatePythonComponentAt(runtime, null);

        Guid CreatePythonComponentAt(PythonRuntime runtime, System.Drawing.PointF? pivot)
        {
            var guid = runtime == PythonRuntime.IronPython2 ? IronPython2Guid : CPython3Guid;
            var obj = Instances.ComponentServer.EmitObject(guid)
                ?? throw new InvalidOperationException(
                    $"Could not emit a {runtime} component ({guid}). Is the RhinoCode plugin loaded (Rhino 8 SR18+)?");
            var doc = Doc();
            obj.CreateAttributes();
            if (obj.Attributes != null) // given pivot (the swap), else placed sensibly (cascade)
                obj.Attributes.Pivot = pivot ?? new System.Drawing.PointF(50f + doc.ObjectCount * 25f, 50f);
            doc.AddObject(obj, false);
            return obj.InstanceGuid;
        }

        public ConvertStagedResult ConvertStaged(
            Guid socketId, string code, IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime, string? nicknameSlug, IReadOnlyList<IoParamSpec>? inputs)
        {
            var doc = Doc();
            var socket = AsComponent(Find(doc, socketId));
            if (socket.ComponentGuid != WireifyIds.SocketComponentGuid)
                throw new InvalidOperationException(
                    $"Component {socketId} is not a Wireify socket (it is '{socket.Name}'). " +
                    "Use set_source to edit an existing Python component in place.");

            WireifyIds.TryParseNumber(socket.NickName, out var number);
            var pivot = socket.Attributes?.Pivot;
            var staged = socket.Params.Input
                .Select(p => (Name: ParamKey(p), Sources: p.Sources.ToList()))
                .ToList();

            // Everything is validated BEFORE the document is touched: a refusal changes nothing.
            var io = StagedConversion.ValidateIo(staged.Select(s => s.Name).ToList(), inputs, outputs);
            if (io.Error is not null)
            {
                return new ConvertStagedResult(
                    false, Guid.Empty, socket.NickName ?? "",
                    Array.Empty<string>(), staged.Select(s => s.Name).ToList(),
                    Array.Empty<string>(), io.Error);
            }

            // Build the replacement first — the socket stays untouched until the swap is safe.
            var newId = CreatePythonComponentAt(runtime, pivot);
            var newComp = AsComponent(Find(doc, newId));
            try
            {
                // Nickname first: SetSource auto-stamps the provenance header on W-numbered
                // components, so the identity must be in place before the code lands.
                var nick = number > 0
                    ? WireifyIds.MakeNickname(number, StagedConversion.Slugify(nicknameSlug))
                    : null;
                if (nick is not null) newComp.NickName = nick;

                // Params are constructed explicitly (the sanctioned ScriptVariableParam recipe) —
                // plain script-mode never derives inputs from source, so nothing is parsed.
                BuildScriptIo(newComp, io.Inputs, io.Outputs);
                SetSource(newId, code, runtime, solve: false); // wires land below; one solve at the end

                // The swap, as one undo record: add, rewire, remove — ctrl-Z restores the socket.
                var record = new GH_UndoRecord("Wireify convert");
                record.AddAction(new GH_AddObjectAction(newComp));

                var wired = new List<string>();
                foreach (var (name, sources) in staged)
                {
                    if (sources.Count == 0) continue;
                    var target = newComp.Params.Input.First(
                        p => string.Equals(ParamKey(p), name, StringComparison.OrdinalIgnoreCase));
                    record.AddAction(new GH_WireAction(target));
                    foreach (var source in sources) target.AddSource(source);
                    wired.Add(name);
                }

                foreach (var param in socket.Params.Input)
                    record.AddAction(new GH_WireAction(param));
                record.AddAction(new GH_RemoveObjectAction(socket));
                doc.RemoveObject(socket, false);

                if (nick is not null) newComp.Attributes?.ExpireLayout();

                doc.UndoServer.PushUndoRecord(record);

                if (newComp is IGH_ActiveObject active) active.ExpireSolution(true);
                doc.NewSolution(false);

                return new ConvertStagedResult(
                    true, newId, newComp.NickName ?? "",
                    wired, newComp.Params.Input.Select(ParamKey).ToList(),
                    newComp.Params.Output.Select(ParamKey).ToList(),
                    null,
                    BuildReport(doc, newComp, includeDocument: false));
            }
            catch
            {
                try { doc.RemoveObject(newComp, false); } catch { /* best-effort rollback */ }
                throw;
            }
        }

        public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs)
        {
            var comp = AsComponent(Find(Doc(), id));

            var inputSpecs = inputs ?? Array.Empty<IoParamSpec>();
            var io = StagedConversion.ValidateIo(
                inputSpecs.Select(i => i.Name).ToList(), inputSpecs, outputs);
            if (io.Error is not null) throw new ArgumentException(io.Error);

            // Preserve wires on inputs whose name survives the redefinition.
            var oldSources = comp.Params.Input.ToDictionary(
                ParamKey, p => p.Sources.ToList(), StringComparer.OrdinalIgnoreCase);

            BuildScriptIo(comp, io.Inputs, io.Outputs);

            foreach (var param in comp.Params.Input)
                if (oldSources.TryGetValue(ParamKey(param), out var sources))
                    foreach (var source in sources)
                        param.AddSource(source);

            if (comp is IGH_ActiveObject active) active.ExpireSolution(true);
            return Introspect(comp);
        }

        /// <summary>Replace a script component's variable params with the given specs. The stdout
        /// "out" output is kept; everything else is rebuilt via ScriptVariableParam.</summary>
        void BuildScriptIo(IGH_Component comp, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs)
        {
            foreach (var p in comp.Params.Input.ToList())
                comp.Params.UnregisterInputParameter(p, true);
            foreach (var p in comp.Params.Output.ToList())
                if (!string.Equals(ParamKey(p), "out", StringComparison.OrdinalIgnoreCase))
                    comp.Params.UnregisterOutputParameter(p, true);

            foreach (var spec in inputs)
            {
                var param = RhinoCodeInterop.CreateScriptVariableParam(
                    comp, spec.Name, AccessFrom(spec.Access), optional: true, spec.TypeHint);
                comp.Params.RegisterInputParam(param);
            }
            foreach (var spec in outputs)
            {
                var param = RhinoCodeInterop.CreateScriptVariableParam(
                    comp, spec.Name, AccessFrom(spec.Access), optional: false, spec.TypeHint);
                comp.Params.RegisterOutputParam(param);
            }

            RhinoCodeInterop.VariableParameterMaintenance(comp);
            comp.Params.OnParametersChanged();
            comp.Attributes?.ExpireLayout();
        }

        static GH_ParamAccess AccessFrom(string access) => access switch
        {
            "list" => GH_ParamAccess.list,
            "tree" => GH_ParamAccess.tree,
            _ => GH_ParamAccess.item,
        };

        static string ParamKey(IGH_Param p)
            => string.IsNullOrEmpty(p.NickName) ? p.Name ?? "" : p.NickName!;

        public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true)
        {
            var doc = Doc();
            var obj = Find(doc, id);

            // W-numbered components keep their provenance header across every revision — the
            // stamp is mechanical, not something the agent has to remember to preserve.
            if (WireifyIds.TryParseNumber(obj.NickName, out _))
                source = StagedConversion.StampHeader(source, obj.NickName!);

            if (runtime == PythonRuntime.IronPython2)
                RhinoCodeInterop.SetIronPythonCode(obj, source);
            else
                RhinoCodeInterop.SetSource(obj, EnsureDirective(source));
            RhinoCodeInterop.ReBuild(obj, _rebuildTimeout);

            // Solve-and-report by default: a revision's outputs are read fresh in the same call,
            // never stale (the round-4 lesson, made mechanical).
            if (!solve) return null;
            if (obj is IGH_ActiveObject active) active.ExpireSolution(true);
            doc.NewSolution(false);
            return BuildReport(doc, obj, includeDocument: false);
        }

        public void SetParametersFromScript(Guid id) => RhinoCodeInterop.SetParametersFromScript(Find(Doc(), id));

        public void Wire(Guid fromId, int fromOutput, Guid toId, int toInput)
        {
            var doc = Doc();
            var from = AsComponent(Find(doc, fromId));
            var to = AsComponent(Find(doc, toId));
            if (fromOutput < 0 || fromOutput >= from.Params.Output.Count)
                throw new ArgumentOutOfRangeException(nameof(fromOutput));
            if (toInput < 0 || toInput >= to.Params.Input.Count)
                throw new ArgumentOutOfRangeException(nameof(toInput));
            to.Params.Input[toInput].AddSource(from.Params.Output[fromOutput]);
        }

        // --- Run + read --------------------------------------------------------------------

        public RunResult Run(Guid id)
        {
            var doc = Doc();
            var obj = Find(doc, id);
            if (obj is IGH_ActiveObject active) active.ExpireSolution(true);
            doc.NewSolution(false);

            var runCount = -1;
            if (obj.GetType().GetProperty("RunCount")?.GetValue(obj) is int n) runCount = n;
            return new RunResult(true, runCount, BuildReport(doc, obj, includeDocument: false));
        }

        public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false)
        {
            var doc = Doc();
            return BuildReport(doc, Find(doc, id), includeDocument);
        }

        RuntimeReport BuildReport(GH_Document doc, IGH_DocumentObject obj, bool includeDocument)
        {
            var messages = new List<RuntimeMessage>();
            if (obj is IGH_ActiveObject active) CollectMessages(active, messages);
            if (includeDocument)
                foreach (var other in doc.Objects.OfType<IGH_ActiveObject>())
                    if (!ReferenceEquals(other, obj)) CollectMessages(other, messages);

            var outputs = new List<OutputValue>();
            if (obj is IGH_Component comp)
                foreach (var op in comp.Params.Output)
                    outputs.Add(new OutputValue(
                        op.Name ?? "",
                        op.VolatileData.AllData(true).Select(d => d?.ToString() ?? "").ToList()));

            return new RuntimeReport(messages, outputs);
        }

        // --- Helpers -----------------------------------------------------------------------

        static IGH_DocumentObject Find(GH_Document doc, Guid id) =>
            doc.FindObject(id, true) ?? throw new ArgumentException($"No object with id {id} in the document.", nameof(id));

        static IGH_Component AsComponent(IGH_DocumentObject obj) =>
            obj as IGH_Component ?? throw new InvalidOperationException($"Object {obj.InstanceGuid} is not a component.");

        static ComponentIntrospection Introspect(IGH_Component comp) => new(
            comp.InstanceGuid,
            comp.Name ?? "",
            comp.NickName ?? "",
            comp.Params.Input.Select(ToParamInfo).ToList(),
            comp.Params.Output.Select(ToParamInfo).ToList());

        static ParamInfo ToParamInfo(IGH_Param p) =>
            new(p.Name ?? "", p.NickName ?? "", AccessOf(p), p.TypeName ?? "", p.Optional);

        static string AccessOf(IGH_Param p) => p.Access.ToString().ToLowerInvariant();

        static string ClrTypeOf(object item)
        {
            try
            {
                if (item is IGH_Goo goo) return goo.ScriptVariable()?.GetType().FullName ?? goo.GetType().FullName ?? "";
                return item.GetType().FullName ?? "";
            }
            catch { return item.GetType().FullName ?? ""; }
        }

        static void CollectMessages(IGH_ActiveObject obj, List<RuntimeMessage> sink)
        {
            foreach (var level in new[] { GH_RuntimeMessageLevel.Error, GH_RuntimeMessageLevel.Warning, GH_RuntimeMessageLevel.Remark })
                foreach (var text in obj.RuntimeMessages(level))
                    sink.Add(new RuntimeMessage(level.ToString(), text));
        }

        static bool HasProxy(Guid guid) =>
            Instances.ComponentServer.ObjectProxies.Any(p => p.Guid == guid);

        static string EnsureDirective(string source)
        {
            if (source.TrimStart().StartsWith(Python3Directive, StringComparison.Ordinal)) return source;
            return Python3Directive + "\n" + source;
        }
    }
}
