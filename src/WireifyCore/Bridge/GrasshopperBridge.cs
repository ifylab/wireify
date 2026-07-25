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

        readonly SessionDocumentResolver _docs;
        readonly TimeSpan _rebuildTimeout;

        public GrasshopperBridge(SessionDocumentResolver docs, TimeSpan? rebuildTimeout = null)
        {
            _docs = docs ?? throw new ArgumentNullException(nameof(docs));
            _rebuildTimeout = rebuildTimeout ?? TimeSpan.FromSeconds(30);
        }

        /// <summary>Active-document-only convenience (no session routing) — dev/test wiring.</summary>
        public GrasshopperBridge(Func<GH_Document?> activeDocument, TimeSpan? rebuildTimeout = null)
            : this(new SessionDocumentResolver(activeDocument), rebuildTimeout)
        {
        }

        /// <summary>The document this call operates on — the calling session's bound definition
        /// (routed via <see cref="SessionDocumentResolver"/>), or the active canvas for clients
        /// without a session. Mutating calls pass <paramref name="mutate"/> so a bound-but-
        /// background document refuses instead of changing a canvas the user is not looking at.</summary>
        GH_Document Doc(bool mutate = false) => _docs.Resolve(mutate);

        // --- Orientation -------------------------------------------------------------------

        public DocumentSummary GetDocumentSummary(
            bool includeStagedData = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents,
            string? nameFilter = null)
        {
            var doc = Doc();
            // Bounded for production-size canvases: the pure SummaryBounding applies the filter
            // and the priority cap (selected + Wireify-managed kept first); the registry below is
            // never truncated.
            var candidates = doc.Objects
                .Select(o => new SummaryCandidate(
                    new ComponentRef(o.InstanceGuid, o.Name ?? "", o.NickName ?? ""),
                    o.Attributes is { Selected: true },
                    IsWireifyPriority(o)))
                .ToList();
            var (components, truncated) = SummaryBounding.Apply(candidates, maxComponents, nameFilter);

            return new DocumentSummary(
                string.IsNullOrEmpty(doc.FilePath) ? null : doc.FilePath,
                components,
                ScanWireify(doc, includeStagedData),
                doc.ObjectCount,
                truncated,
                _docs.IsActive(doc));
        }

        static bool IsWireifyPriority(IGH_DocumentObject obj) =>
            (obj is IGH_Component c && c.ComponentGuid == WireifyIds.SocketComponentGuid)
            || WireifyIds.TryParseNumber(obj.NickName, out _);

        /// <summary>The Wireify registry, derived from the document itself: sockets by
        /// ComponentGuid, converted components by the <c>W&lt;n&gt;</c> nickname convention.
        /// Also feeds the WIREIFY_NOT_FOUND error, so stale ids self-repair in-band.</summary>
        static List<WireifyComponentInfo> ScanWireify(GH_Document doc, bool includeStagedData)
        {
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
            return wireify.OrderBy(w => w.Number).ToList();
        }

        public ComponentIntrospection IntrospectComponent(Guid id) => IntrospectObject(Find(Doc(), id));

        public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
        {
            return Doc().Objects
                .Where(o => o.Attributes is { Selected: true })
                .Where(o => o is IGH_Component or IGH_Param)
                .Select(IntrospectObject)
                .ToList();
        }

        /// <summary>Components introspect as usual; a floating param (panel, slider, value list)
        /// reports itself as its own single output — it feeds downstream wires, so it is a
        /// legitimate introspection target, not a refusal.</summary>
        static ComponentIntrospection IntrospectObject(IGH_DocumentObject obj) => obj switch
        {
            IGH_Component comp => Introspect(comp),
            IGH_Param param => new ComponentIntrospection(
                param.InstanceGuid, param.Name ?? "", param.NickName ?? "",
                Array.Empty<ParamInfo>(), new[] { ToParamInfo(param) }),
            _ => throw new InvalidOperationException(
                $"Object {obj.InstanceGuid} ('{obj.Name}') is neither a component nor a param — nothing to introspect."),
        };

        public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50)
        {
            var comp = AsComponent(Find(Doc(), id));
            var param = comp.Params.Input.FirstOrDefault(p => p.Name == inputParam || p.NickName == inputParam)
                ?? throw new ArgumentException($"No input '{inputParam}' on component {id}.", nameof(inputParam));
            return ShapeParamData(param, param.Name ?? inputParam, maxPerBranch, maxTotal);
        }

        static InputData ShapeParamData(IGH_Param param, string reportName, int maxPerBranch, int maxTotal,
            int maxValueChars = InputDataShaper.MaxSampleValueChars)
        {
            // Read VolatileData into a plain representation, then let the pure shaper do the
            // histogram / sampling / tree stats (so that logic is unit-tested without Rhino).
            // This runs on the UI thread, so per-item work is budgeted: the expensive calls
            // (ScriptVariable for the CLR type, ToString for the value) happen once per distinct
            // TypeName and only for items the shaper will actually sample; everything else is a
            // cheap TypeName read. Large wires must never pin Rhino.
            var volatileData = param.VolatileData;
            var branches = new List<ShapedBranch>();
            var typeInfoByName = new Dictionary<string, (string Clr, string Goo)>(StringComparer.Ordinal);
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
                    if (!typeInfoByName.TryGetValue(typeName, out var info))
                    {
                        info = (ClrTypeOf(item), goo?.GetType().Name ?? "");
                        typeInfoByName[typeName] = info;
                    }

                    // Mirrors InputDataShaper's selection (first N per branch, first M overall, in
                    // order), so a value string exists exactly where a sample will be taken. The
                    // shaper caps the value for transport and reports its full length.
                    var value = "";
                    if (sampledInBranch < maxPerBranch && sampledTotal < maxTotal)
                    {
                        value = goo?.ToString() ?? item.ToString() ?? "";
                        sampledInBranch++;
                        sampledTotal++;
                    }
                    items.Add(new ShapedItem(typeName, info.Clr, info.Goo, value));
                }
                branches.Add(new ShapedBranch(path.ToString(), items));
            }

            return InputDataShaper.Shape(reportName, AccessOf(param), branches, maxPerBranch, maxTotal, maxValueChars);
        }

        public RuntimeInfo GetRuntimeInfo()
        {
            var version = RhinoApp.Version?.ToString() ?? "unknown";
            var runtimes = new List<string>();
            if (HasProxy(CPython3Guid)) runtimes.Add("cpython3");
            if (HasProxy(IronPython2Guid)) runtimes.Add("ironpython2");
            return new RuntimeInfo(version, runtimes, "unknown", RhinoCodeAssembliesLoaded(), WireifyBuild.Describe());
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
            var doc = Doc(mutate: true);
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
            var doc = Doc(mutate: true);
            var socket = AsComponent(Find(doc, socketId));
            if (socket.ComponentGuid != WireifyIds.SocketComponentGuid)
                throw new InvalidOperationException(ErrorProtocol.NotASocket(socketId, socket.Name ?? ""));

            WireifyIds.TryParseNumber(socket.NickName, out var number);
            var pivot = socket.Attributes?.Pivot;
            var staged = socket.Params.Input
                .Select(p => (Name: ParamKey(p), Sources: p.Sources.ToList()))
                .ToList();
            // Observed while the socket still holds the wires: what actually flows per input
            // drives the auto-hint (the registry has no dynamic entry, so "no hint" must be an
            // informed choice, not a silent fallback into Guid marshaling).
            var observedByName = socket.Params.Input.ToDictionary(
                ParamKey, ObservedClrTypes, StringComparer.OrdinalIgnoreCase);

            // A staged input nothing was ever wired into is dropped here unless the caller names
            // it explicitly — the socket's spare default input must not survive conversion as a
            // permanently dead param. Coverage below applies to what remains.
            var stagedNames = staged.Select(s => s.Name).ToList();
            var selection = StagedConversion.SelectConversionInputs(
                stagedNames,
                staged.Where(s => s.Sources.Count > 0).Select(s => s.Name).ToList(),
                inputs);

            // Everything is validated BEFORE the document is touched: a refusal changes nothing.
            var io = StagedConversion.ValidateIo(selection.Effective, inputs, outputs);
            if (io.Error is not null)
            {
                return new ConvertStagedResult(
                    false, Guid.Empty, socket.NickName ?? "",
                    Array.Empty<string>(), stagedNames,
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
                var (inputSpecs, hintWarnings) = ResolveInputHints(newComp, io.Inputs, observedByName);
                BuildScriptIo(newComp, inputSpecs, io.Outputs);
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

                var warnings = new List<string>(hintWarnings);
                if (selection.DroppedUnwired.Count > 0)
                    warnings.Add(StagedConversion.DroppedUnwiredNote(selection.DroppedUnwired));

                return new ConvertStagedResult(
                    true, newId, newComp.NickName ?? "",
                    wired, newComp.Params.Input.Select(ParamKey).ToList(),
                    newComp.Params.Output.Select(ParamKey).ToList(),
                    null,
                    BuildReport(doc, newComp, includeDocument: false),
                    warnings.Count > 0 ? warnings : null);
            }
            catch
            {
                try { doc.RemoveObject(newComp, false); } catch { /* best-effort rollback */ }
                throw;
            }
        }

        /// <summary>Auto-hint the input specs the caller left un-hinted, from the data observed on
        /// the socket: one mappable CLR type selects its concrete token (verified against the new
        /// component's own hint registry via a probe param), a mixed tree selects nothing and
        /// warns. Explicit hints pass through untouched.</summary>
        (IReadOnlyList<IoParamSpec> Specs, IReadOnlyList<string> Warnings) ResolveInputHints(
            IGH_Component newComp,
            IReadOnlyList<IoParamSpec> inputs,
            IReadOnlyDictionary<string, (IReadOnlyList<string> Types, bool Mixed)> observedByName)
        {
            IReadOnlyList<string>? available = null;
            IReadOnlyList<string> AvailableHints()
            {
                if (available is not null) return available;
                try
                {
                    // A probe param, constructed and discarded — never registered on the component.
                    var probe = RhinoCodeInterop.CreateScriptVariableParam(
                        newComp, "probe", GH_ParamAccess.item, optional: true, typeHint: null);
                    available = RhinoCodeInterop.GetAvailableHintNames(probe);
                }
                catch { available = Array.Empty<string>(); }
                return available;
            }

            var specs = new List<IoParamSpec>(inputs.Count);
            var warnings = new List<string>();
            foreach (var spec in inputs)
            {
                if (!string.IsNullOrWhiteSpace(spec.TypeHint)
                    || !observedByName.TryGetValue(spec.Name, out var observed)
                    || observed.Types.Count == 0)
                {
                    specs.Add(spec);
                    continue;
                }

                if (observed.Mixed)
                {
                    warnings.Add(HintSelection.MixedTreeWarning(spec.Name, observed.Types));
                    specs.Add(spec);
                    continue;
                }

                var token = HintSelection.AutoHint(observed.Types[0]);
                if (token is not null && AvailableHints() is { Count: > 0 } hints)
                    token = HintSelection.Resolve(token, hints); // absent on this build -> stay un-hinted
                specs.Add(token is null ? spec : spec with { TypeHint = token });
            }
            return (specs, warnings);
        }

        /// <summary>Distinct CLR types observed on a param's volatile data (bounded scan), plus
        /// whether more than one is present — the auto-hint's evidence.</summary>
        static (IReadOnlyList<string> Types, bool Mixed) ObservedClrTypes(IGH_Param param)
        {
            const int maxItems = 64;
            var types = new List<string>();
            var clrByTypeName = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var scanned = 0;
            foreach (var path in param.VolatileData.Paths)
            {
                foreach (var item in param.VolatileData.get_Branch(path))
                {
                    if (item is null) continue;
                    if (scanned++ >= maxItems) return (types, types.Count > 1);
                    var goo = item as IGH_Goo;
                    var typeName = goo?.TypeName ?? item.GetType().Name;
                    if (!clrByTypeName.TryGetValue(typeName, out var clr))
                    {
                        clr = ClrTypeOf(item);
                        clrByTypeName[typeName] = clr;
                    }
                    if (!string.IsNullOrEmpty(clr) && seen.Add(clr)) types.Add(clr);
                }
            }
            return (types, types.Count > 1);
        }

        public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs)
        {
            var comp = AsComponent(Find(Doc(mutate: true), id));

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
            // The raw selected-hint read is best-effort against a private registry — the specs
            // just applied are authoritative for the echo.
            return HintSelection.ApplyDeclaredHints(Introspect(comp), io.Inputs, io.Outputs);
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

        public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true, bool overwriteExternalEdits = false)
        {
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);

            // W-numbered components keep their provenance header across every revision — the
            // stamp is mechanical, not something the agent has to remember to preserve. The
            // stamp's body fingerprint doubles as the drift guard: code hand-edited outside
            // Wireify (the GH script editor) refuses a blind overwrite, handing the current
            // source back for a merge; overwriteExternalEdits is the deliberate-discard path.
            if (WireifyIds.TryParseNumber(obj.NickName, out _))
            {
                if (!overwriteExternalEdits
                    && RhinoCodeInterop.TryGetSource(obj, out var current)
                    && StagedConversion.IsExternallyEdited(current))
                    throw new InvalidOperationException(ErrorProtocol.ExternalEdit(obj.NickName ?? "", id, current));
                source = StagedConversion.StampHeader(source, obj.NickName!);
            }

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

        public void SetParametersFromScript(Guid id) => RhinoCodeInterop.SetParametersFromScript(Find(Doc(mutate: true), id));

        public DeletedComponent DeleteComponent(Guid id)
        {
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            var deletable =
                (obj is IGH_Component socket && socket.ComponentGuid == WireifyIds.SocketComponentGuid)
                || RhinoCodeInterop.TryGetSource(obj, out _);
            if (!deletable)
                throw new InvalidOperationException(
                    $"Object {id} ('{obj.Name}', nickname '{obj.NickName}') is not Wireify-managed — only Wireify " +
                    "sockets and script components can be deleted here. Remove anything else manually in Grasshopper.");

            var name = obj.Name ?? "";
            var nick = obj.NickName ?? "";

            // One undo record, wires included: snapshot every affected param before the removal so
            // ctrl-Z restores the object with its connections (the convert_staged pattern).
            var record = new GH_UndoRecord("Wireify delete");
            if (obj is IGH_Component comp)
            {
                foreach (var p in comp.Params.Input)
                    record.AddAction(new GH_WireAction(p));
                foreach (var op in comp.Params.Output)
                    foreach (var recipient in op.Recipients.ToList())
                        record.AddAction(new GH_WireAction(recipient));
            }
            record.AddAction(new GH_RemoveObjectAction(obj));

            doc.RemoveObject(obj, false);
            doc.UndoServer.PushUndoRecord(record);
            doc.NewSolution(false);

            return new DeletedComponent(id, name, nick);
        }

        public WireResult Wire(Guid fromId, int fromOutput, Guid toId, int toInput, WireMode mode = WireMode.Strict)
        {
            var doc = Doc(mutate: true);
            var source = ResolveWireParam(Find(doc, fromId), fromOutput, outputSide: true);
            var target = ResolveWireParam(Find(doc, toId), toInput, outputSide: false);

            // The occupancy guard (the round-15 contamination fix): under Strict an occupied
            // input refuses BEFORE anything changes — merging branches is only ever an explicit
            // 'add', swapping wires an explicit 'replace'.
            var existing = target.Sources.ToList();
            if (mode == WireMode.Strict && existing.Count > 0)
                throw new InvalidOperationException(
                    ErrorProtocol.InputWired(ParamKey(target), toId, existing.Select(WireEnd).ToList()));

            // One undo record either way: GH_WireAction snapshots the target's wiring before the
            // mutation, so ctrl-Z restores the previous state (replace included) in one step.
            var record = new GH_UndoRecord("Wireify wire");
            record.AddAction(new GH_WireAction(target));

            var replaced = new List<WireEndInfo>();
            if (mode == WireMode.Replace && existing.Count > 0)
            {
                replaced.AddRange(existing.Select(WireEnd));
                target.RemoveAllSources();
            }
            target.AddSource(source);
            doc.UndoServer.PushUndoRecord(record);

            // Interactive wiring recomputes the canvas; the tool matches that, so a read right
            // after a wire sees live data instead of an empty pre-solve preview (round-17 finding:
            // volatile data stays empty until a solve — re-reading alone never refreshes it).
            target.ExpireSolution(false);
            doc.NewSolution(false);

            return new WireResult(fromId, ParamKey(source), toId, ParamKey(target), ModeString(mode), replaced);
        }

        static string ModeString(WireMode mode) => mode switch
        {
            WireMode.Add => "add",
            WireMode.Replace => "replace",
            _ => "strict",
        };

        /// <summary>A wire end: a component's indexed param, or a floating param (panel, slider,
        /// file path) — which IS its own single param on either side, so index 0 addresses it.</summary>
        static IGH_Param ResolveWireParam(IGH_DocumentObject obj, int index, bool outputSide)
        {
            switch (obj)
            {
                case IGH_Component comp:
                    var list = outputSide ? comp.Params.Output : comp.Params.Input;
                    if (index < 0 || index >= list.Count)
                        throw new ArgumentOutOfRangeException(outputSide ? "fromOutput" : "toInput",
                            $"Component {obj.InstanceGuid} ('{obj.Name}') has {list.Count} {(outputSide ? "outputs" : "inputs")}.");
                    return list[index];
                case IGH_Param param:
                    if (index != 0)
                        throw new ArgumentException(
                            $"Object {obj.InstanceGuid} ('{obj.Name}') is a floating param — it is its own single " +
                            $"{(outputSide ? "output" : "input")}; pass index 0.");
                    return param;
                default:
                    throw new InvalidOperationException(
                        $"Object {obj.InstanceGuid} ('{obj.Name}') cannot be wired — not a component or param.");
            }
        }

        public PanelText SetPanelText(Guid id, string text)
        {
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            if (obj is not Grasshopper.Kernel.Special.GH_Panel panel)
                throw new InvalidOperationException(
                    $"Object {id} ('{obj.Name}', nickname '{obj.NickName}') is not a Panel — set_panel_text writes only Panel components.");

            var record = new GH_UndoRecord("Wireify set panel text");
            record.AddAction(new GH_GenericObjectAction(panel));
            panel.UserText = text;
            doc.UndoServer.PushUndoRecord(record);
            panel.ExpireSolution(true);
            doc.NewSolution(false);
            return new PanelText(id, panel.NickName ?? "", text.Length);
        }

        // --- Run + read --------------------------------------------------------------------

        public RunResult Run(Guid id)
        {
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            // Expire without an immediate recompute, then one NewSolution — the report below
            // must read volatile data the single solve has finished writing, never a param
            // caught cleared between two solves.
            if (obj is IGH_ActiveObject active) active.ExpireSolution(false);
            doc.NewSolution(false);

            // Only script components expose a run counter; -1 on natives and params means
            // "no counter here", not a failed solve.
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
            void AddOutput(IGH_Param p)
            {
                // Outputs are shaped exactly like read inputs (outputs are IGH_Param too):
                // tree stats + type histogram + capped samples, with ToString budgeted to the
                // sampled items only — a heavy output must not pin the UI thread or flood the
                // response, and the agent can verify tree preservation from the report itself.
                var shaped = ShapeParamData(p, p.Name ?? "",
                    InputDataShaper.MaxReportPerBranch,
                    InputDataShaper.MaxReportValues,
                    InputDataShaper.MaxReportValueChars);
                outputs.Add(new OutputValue(p.Name ?? "", shaped.Tree, shaped.Types,
                    shaped.Samples, shaped.Tree.DataCount, shaped.Warnings));
            }
            switch (obj)
            {
                case IGH_Component comp:
                    foreach (var op in comp.Params.Output) AddOutput(op);
                    break;
                case IGH_Param param:
                    // A floating param (panel, slider, leaf param container) is its own single
                    // output — the same rule introspection applies, so run/read on a leaf param
                    // reports its live value instead of an empty report.
                    AddOutput(param);
                    break;
            }

            return new RuntimeReport(messages, outputs);
        }

        // --- Helpers -----------------------------------------------------------------------

        static IGH_DocumentObject Find(GH_Document doc, Guid id) =>
            doc.FindObject(id, true) ?? throw new ArgumentException(
                ErrorProtocol.NotFound(id, ScanWireify(doc, includeStagedData: false)), nameof(id));

        static IGH_Component AsComponent(IGH_DocumentObject obj) =>
            obj as IGH_Component ?? throw new InvalidOperationException($"Object {obj.InstanceGuid} is not a component.");

        static ComponentIntrospection Introspect(IGH_Component comp) => new(
            comp.InstanceGuid,
            comp.Name ?? "",
            comp.NickName ?? "",
            comp.Params.Input.Select(ToParamInfo).ToList(),
            comp.Params.Output.Select(ToParamInfo).ToList());

        static ParamInfo ToParamInfo(IGH_Param p)
        {
            // TypeName never reflects a script param's hint (it is the goo type, "Generic Data"
            // on every script variable), so the hint is reported explicitly — with the available
            // names, so a caller choosing one picks from reality instead of guessing.
            var availableHints = RhinoCodeInterop.GetAvailableHintNames(p);
            // Live wiring, both directions — the mechanical answer to "is this component spare?".
            // Ledger claims about wiring go stale between sessions; this read never does.
            var (sources, sourceCount) = WireEnds(p.Sources);
            var (recipients, recipientCount) = WireEnds(p.Recipients);
            return new ParamInfo(
                p.Name ?? "", p.NickName ?? "", AccessOf(p), p.TypeName ?? "", p.Optional,
                RhinoCodeInterop.GetSelectedHintName(p),
                availableHints.Count > 0 ? availableHints : null,
                sources, recipients, sourceCount, recipientCount);
        }

        const int MaxWireEndsReported = 50;

        static (IReadOnlyList<WireEndInfo>? Ends, int Count) WireEnds(IEnumerable<IGH_Param> connected)
        {
            List<WireEndInfo>? ends = null;
            var count = 0;
            foreach (var p in connected)
            {
                count++;
                if (count > MaxWireEndsReported) continue; // count the fanout, never flood the payload
                (ends ??= new List<WireEndInfo>()).Add(WireEnd(p));
            }
            return (ends, count);
        }

        /// <summary>A wire end as reported: the param's owner (resolved via the top-level
        /// attribute object; a floating param is its own owner) plus the param's key.</summary>
        static WireEndInfo WireEnd(IGH_Param p)
        {
            var owner = p.Attributes?.GetTopLevel?.DocObject ?? (IGH_DocumentObject)p;
            return new WireEndInfo(owner.InstanceGuid, owner.NickName ?? "", ParamKey(p));
        }

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
