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

            var registry = ScanWireify(doc, includeStagedData);
            return new DocumentSummary(
                // "" not null for an unsaved doc: the property is required in the generated
                // output schema and the SDK serializes nulls away (the S5.6g class).
                doc.FilePath ?? "",
                components,
                registry,
                doc.ObjectCount,
                truncated,
                _docs.IsActive(doc),
                doc.IsModified,
                SavedAtOf(doc),
                RegistryWarnings.DuplicateNumbers(registry));
        }

        /// <summary>The saved file's UTC mtime as ISO-8601, null for a never-saved document or an
        /// unreadable path — the second half of the "is this drift or just unsaved?" answer.</summary>
        static string? SavedAtOf(GH_Document doc)
        {
            try
            {
                var path = doc.FilePath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                return System.IO.File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        static bool IsWireifyPriority(IGH_DocumentObject obj) =>
            (obj is IGH_Component c && c.ComponentGuid == WireifyIds.SocketComponentGuid)
            || WireifyIds.TryParseNumber(obj.NickName, out _);

        /// <summary>The Wireify registry, derived from the document itself: sockets by
        /// ComponentGuid, converted components by the <c>W&lt;n&gt;</c> nickname convention, and
        /// — so a rename cannot make Wireify forget its own work (round-6 S6.8a) — components in
        /// the document's badge record without a <c>W&lt;n&gt;</c> name ("authored", unnumbered,
        /// listed last). The badge and delete_component already tracked renamed components; the
        /// registry was the odd one out. Also feeds the WIREIFY_NOT_FOUND error, so stale ids
        /// self-repair in-band.</summary>
        static List<WireifyComponentInfo> ScanWireify(GH_Document doc, bool includeStagedData)
        {
            HashSet<Guid> touched;
            try { touched = WireifyTouched.Parse(doc.ValueTable.GetValue(WireifyTouched.Key, "")); }
            catch { touched = new HashSet<Guid>(); }

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
                else if (touched.Contains(comp.InstanceGuid))
                {
                    wireify.Add(new WireifyComponentInfo(
                        0, comp.InstanceGuid, comp.NickName ?? "", "authored",
                        comp.Params.Input.Select(p => p.NickName ?? "").ToList()));
                }
            }
            return wireify.OrderBy(w => w.Number == 0 ? int.MaxValue : w.Number).ToList();
        }

        public ComponentIntrospection IntrospectComponent(Guid id) => IntrospectObject(Find(Doc(), id));

        public DocumentGraph GetDocumentGraph(
            IReadOnlyList<Guid>? ids = null,
            bool includeParams = true,
            bool includeOutputs = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents,
            string? nameFilter = null)
        {
            var doc = Doc();
            var objects = doc.Objects.Where(o => o is IGH_Component or IGH_Param).ToList();
            List<IGH_DocumentObject> kept;
            var truncated = false;
            List<Guid>? missing = null;
            if (ids is { Count: > 0 })
            {
                var wanted = new HashSet<Guid>(ids);
                kept = objects.Where(o => wanted.Contains(o.InstanceGuid)).ToList();
                var found = new HashSet<Guid>(kept.Select(o => o.InstanceGuid));
                missing = ids.Where(id => !found.Contains(id)).Distinct().ToList();
                if (missing.Count == 0) missing = null;
            }
            else
            {
                // The summary's bounding, so a production-size canvas answers in one call:
                // selected and Wireify-managed objects kept first, document order preserved.
                var candidates = objects
                    .Select(o => new SummaryCandidate(
                        new ComponentRef(o.InstanceGuid, o.Name ?? "", o.NickName ?? ""),
                        o.Attributes is { Selected: true },
                        IsWireifyPriority(o)))
                    .ToList();
                var (refs, cut) = SummaryBounding.Apply(candidates, maxComponents, nameFilter);
                var keepIds = new HashSet<Guid>(refs.Select(r => r.Id));
                kept = objects.Where(o => keepIds.Contains(o.InstanceGuid)).ToList();
                truncated = cut;
            }

            // Read every kept object once: its wiring both ways, its node, its cut priority. A
            // floating param is ONE param on both sides — its own sources are its inbound
            // wiring (round-12 S12.3: walking components' inputs alone dropped every wire into
            // a panel or a relay param, 19 of 198 on the truss, silently) and its recipients
            // its outbound; as a node it lists that param once, on the output side.
            var entries = new List<(GraphWiring Wiring, GraphNode Node, int Priority)>(kept.Count);
            IReadOnlyList<string>? hints = null;
            foreach (var obj in kept)
            {
                IList<IGH_Param> inputs = obj switch
                {
                    IGH_Component comp => comp.Params.Input,
                    IGH_Param fp => new[] { fp },
                    _ => Array.Empty<IGH_Param>(),
                };
                IList<IGH_Param> outputs = obj switch
                {
                    IGH_Component c => c.Params.Output,
                    IGH_Param p => new[] { p },
                    _ => Array.Empty<IGH_Param>(),
                };
                var wiring = new GraphWiring(
                    obj.InstanceGuid,
                    inputs.Select(p => new WiredParam(ParamKey(p), p.Sources.Select(s => WireEnd(s)).ToList())).ToList(),
                    outputs.Select(p => new WiredParam(ParamKey(p), p.Recipients.Select(r => WireEnd(r)).ToList())).ToList());
                // The hint registry is the same list on every script param: read it once, hoist it.
                if (includeParams && hints is null)
                    foreach (var p in inputs.Concat(outputs))
                    {
                        var available = RhinoCodeInterop.GetAvailableHintNames(p);
                        if (available.Count > 0) { hints = available; break; }
                    }
                var listedInputs = obj is IGH_Component ? inputs : Array.Empty<IGH_Param>();
                var node = new GraphNode(
                    obj.InstanceGuid, obj.Name ?? "", obj.NickName ?? "",
                    obj is IGH_Component ? "component" : "param",
                    obj.Category ?? "", obj.SubCategory ?? "",
                    includeParams ? listedInputs.Select(ToGraphParam).ToList() : null,
                    includeParams ? outputs.Select(ToGraphParam).ToList() : null,
                    includeOutputs ? outputs.Select(p => ShapeParamData(p, ParamKey(p), 3, 12)).ToList() : null);
                entries.Add((wiring, node, obj.Attributes is { Selected: true } ? 0 : IsWireifyPriority(obj) ? 1 : 2));
            }

            var truncation = truncated ? $"node cap ({maxComponents}) — narrow with nameFilter or ids" : null;
            var active = _docs.IsActive(doc);
            DocumentGraph Assemble(IReadOnlyList<(GraphWiring Wiring, GraphNode Node, int Priority)> chosen, bool cut, string? why)
                => new(doc.FilePath ?? "",
                    chosen.Select(e => e.Node).ToList(),
                    DocumentGraphBuilder.Edges(chosen.Select(e => e.Wiring).ToList()),
                    doc.ObjectCount, cut, active, missing, why, hints);

            // A read scoped by ids is the agent's own choice and never trimmed. A whole-canvas
            // read stays under the token budget over the WHOLE reply — nodes, edges, hints and
            // envelope — cut in priority order (selected, Wireify-managed, the rest; ties by
            // document order) and answered in document order (round-11 S11.43, round-12 S12.2).
            if (ids is { Count: > 0 }) return Assemble(entries, truncated, truncation);

            var byPriority = entries.Select((e, i) => (e.Priority, i)).OrderBy(x => x.Priority).ThenBy(x => x.i).Select(x => x.i).ToList();
            List<(GraphWiring Wiring, GraphNode Node, int Priority)> First(int k)
            {
                var take = new HashSet<int>(byPriority.Take(k));
                return entries.Where((_, i) => take.Contains(i)).ToList();
            }
            var fit = DocumentGraphBuilder.FitWithinBudget(
                entries.Count, DocumentGraphBuilder.TokenBudget,
                k => DocumentGraphBuilder.EstimateTokens(
                    System.Text.Json.JsonSerializer.Serialize(Assemble(First(k), false, null), GraphSizeOpts)));
            if (fit < entries.Count)
            {
                truncated = true;
                truncation = DocumentGraphBuilder.TruncationNote(fit, entries.Count, includeParams);
            }
            return Assemble(First(fit), truncated, truncation);
        }

        static readonly System.Text.Json.JsonSerializerOptions GraphSizeOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

        static GraphParam ToGraphParam(IGH_Param p)
            => new(p.Name ?? "", p.NickName ?? "", AccessOf(p), p.TypeName ?? "", p.Optional,
                RhinoCodeInterop.GetSelectedHintName(p));

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
                Array.Empty<ParamInfo>(), new[] { ToParamInfo(param) }, BoundsOf(param)),
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

        public Guid CreatePythonComponent(PythonRuntime runtime, string? nickName = null)
            => CreatePythonComponentAt(runtime, null, nickName);

        Guid CreatePythonComponentAt(PythonRuntime runtime, System.Drawing.PointF? pivot, string? nickName = null)
        {
            var guid = runtime == PythonRuntime.IronPython2 ? IronPython2Guid : CPython3Guid;
            var obj = Instances.ComponentServer.EmitObject(guid)
                ?? throw new InvalidOperationException(
                    $"Could not emit a {runtime} component ({guid}). Is the RhinoCode plugin loaded (Rhino 8 SR18+)?");
            var doc = Doc(mutate: true);
            if (!string.IsNullOrWhiteSpace(nickName)) obj.NickName = nickName!.Trim();
            obj.CreateAttributes();
            if (obj.Attributes != null)
            {
                // A given pivot (the convert swap) lands exactly there; the default cascade
                // slides down past whatever occupies its spot — the ObjectCount seed alone
                // parked repeat creates on the same pixel and on user objects (round-7 item 3).
                if (pivot is { } exact)
                {
                    obj.Attributes.Pivot = exact;
                }
                else
                {
                    var seed = new System.Drawing.PointF(50f + doc.ObjectCount * 25f, 50f);
                    var occupied = new List<PlacementRect>();
                    foreach (var existing in doc.Objects)
                        if (existing.Attributes is { Bounds: { Width: > 0 } b })
                            occupied.Add(new PlacementRect(b.X, b.Y, b.Width, b.Height));
                    var (x, y) = CanvasPlacement.Settle(
                        new PlacementRect(seed.X - 60f, seed.Y - 40f, 120f, 80f), occupied);
                    obj.Attributes.Pivot = new System.Drawing.PointF(x + 60f, y + 40f);
                }
            }
            doc.AddObject(obj, false);
            LayOut(obj);
            RecordTouch(doc, obj.InstanceGuid);
            return obj.InstanceGuid;
        }

        /// <summary>Record an object Wireify created or wrote into the document's touched set
        /// (<see cref="WireifyIds"/>' sibling <see cref="WireifyTouched"/> codec, one value-table
        /// key). The badge overlay draws a plain "wireify" capsule from it — the user-visible
        /// answer to "which of these objects did the agent add". Inert core-GH metadata (saves
        /// only when the USER saves, opens stock without Wireify) and best-effort: bookkeeping
        /// never fails a mutation. Append-only — stale guids are harmless and a delete + ctrl-Z
        /// restore keeps its badge. App-surface value pushes deliberately do NOT record: a
        /// browser drag is the user's own hand, not agent authorship.</summary>
        static void RecordTouch(GH_Document doc, Guid id)
        {
            try
            {
                var raw = doc.ValueTable.GetValue(WireifyTouched.Key, "");
                if (WireifyTouched.Append(raw, id) is { } updated)
                    doc.ValueTable.SetValue(WireifyTouched.Key, updated);
            }
            catch { /* badge bookkeeping is best-effort — never fails the mutation */ }
        }

        /// <summary>Remove one object's badge record — the deliberate exit from touch-badging,
        /// which is otherwise permanent (an accidental set_panel_text marks a user's own panel
        /// forever; round-6 S6.28b). Mirrors <see cref="RecordTouch"/>: no undo record either
        /// way, the user's next save persists it, any later touch re-badges. A cleared component
        /// also leaves the "authored" registry listing — clearing means forget the association.
        /// The <c>W&lt;n&gt;</c> NUMBERED capsule follows the nickname, not this record; the
        /// receipt says so when it applies. Works on stale guids too (the object may already be
        /// gone; the record is what is being cleaned).</summary>
        public ClearBadgeResult ClearBadge(Guid id)
        {
            var doc = Doc(mutate: true);
            var obj = doc.FindObject(id, true);
            var nick = obj?.NickName ?? "";
            var raw = doc.ValueTable.GetValue(WireifyTouched.Key, "");
            var updated = WireifyTouched.Remove(raw, id);
            if (updated is not null) doc.ValueTable.SetValue(WireifyTouched.Key, updated);
            try { Instances.RedrawCanvas(); } catch { /* repaint is best-effort */ }
            var numbered = obj is not null && WireifyIds.TryParseNumber(obj.NickName, out _);
            return new ClearBadgeResult(
                Cleared: updated is not null,
                NickName: nick,
                Note: updated is null
                    ? "no badge record for this id — nothing to clear"
                    : numbered
                        ? $"the numbered capsule follows the nickname '{nick}' — rename the component to drop it"
                        : null);
        }

        public RenameResult RenameComponent(Guid id, string nickName)
        {
            // An empty name is the deliberate un-name (the tool gates it behind clear: true —
            // round-8 F5: the only way back to an unnamed control was the user's own ctrl-Z).
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            if (obj is IGH_Component socket && socket.ComponentGuid == WireifyIds.SocketComponentGuid)
                throw new InvalidOperationException(
                    $"{id} is a Wireify socket — sockets are addressed by their number and never renamed here. "
                    + "Convert it (convert_staged names the result through nicknameSlug) and rename that component if needed.");

            var before = obj.NickName ?? "";
            var final = (nickName ?? "").Trim();
            var wasNumbered = WireifyIds.TryParseNumber(before, out var oldNumber);
            var attrs = obj.Attributes;
            LayOut(obj);
            var rectBefore = attrs?.Bounds ?? System.Drawing.RectangleF.Empty;
            var snapshotBefore = RenameLayoutAction.Capture(obj);

            obj.NickName = final;
            // Grasshopper's slider layout is max(current width, fitted width) — it grows for a
            // long name and never shrinks back (S9.32 measured 385 px surviving clear, a 4-char
            // name, and undo). Zero the width first so the name sizes the slider both ways.
            if (obj is Grasshopper.Kernel.Special.GH_NumberSlider && attrs is not null)
                attrs.Bounds = new System.Drawing.RectangleF(attrs.Bounds.X, attrs.Bounds.Y, 0f, attrs.Bounds.Height);
            LayOut(obj);
            var edgeKept = KeepAnchoredEdge(obj, rectBefore);
            RecordTouch(doc, obj.InstanceGuid);

            // ONE undo record holding the name AND the layout on BOTH sides of the rename, applied
            // together: ctrl-Z restores the old name, width and position as a whole (round-9
            // S9.32: the name came back, the width did not; round-10 S10.8: the stock layout
            // action put the pivot back but kept the new width, and the capsule overshot the
            // component it fed by the whole width delta).
            var record = new GH_UndoRecord("Wireify rename");
            record.AddAction(new RenameLayoutAction(obj.InstanceGuid, snapshotBefore, RenameLayoutAction.Capture(obj)));
            doc.UndoServer.PushUndoRecord(record);
            try { Instances.RedrawCanvas(); } catch { /* repaint is best-effort */ }

            var isNumbered = WireifyIds.TryParseNumber(final, out var newNumber);
            string? note = null;
            if (final.Length == 0)
                note = "the object is unnamed now: its app card and report row read by kind until it is named again"
                    + (wasNumbered
                        ? $" (the W{oldNumber} prefix went with the name: 'do #{oldNumber}' no longer resolves here)"
                        : "");
            else if (wasNumbered && !isNumbered)
                note = $"the W{oldNumber} prefix is gone: 'do #{oldNumber}' no longer resolves to this component and the "
                    + $"registry lists it as authored (rename it back to 'W{oldNumber} …' to restore the number)";
            else if (wasNumbered && isNumbered && newNumber != oldNumber)
                note = $"the number changed W{oldNumber} → W{newNumber}: 'do #{newNumber}' now resolves here";
            if (!edgeKept)
                note = (note is null ? "" : note + "; ")
                    + "the object's right edge could not be held while it grew — introspect_component reports where it landed";
            return new RenameResult(id, before, final, note);
        }

        public AppControlState CreateControlComponent(ControlSpec spec)
        {
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            var doc = Doc(mutate: true);

            // The control is built and validated BEFORE the document is touched — a refusal
            // (unknown kind, bad config) changes nothing, the convert_staged discipline.
            IGH_DocumentObject control = (spec.Kind ?? "").Trim().ToLowerInvariant() switch
            {
                "slider" => BuildSlider(spec),
                "panel" => new Grasshopper.Kernel.Special.GH_Panel { UserText = spec.Text ?? "" },
                "toggle" => new Grasshopper.Kernel.Special.GH_BooleanToggle { Value = spec.Flag ?? false },
                "valuelist" => BuildValueList(spec),
                "button" => new Grasshopper.Kernel.Special.GH_ButtonObject(),
                "mdslider" => BuildMdSlider(spec),
                "colour" => BuildColourSwatch(spec),
                "knob" => BuildKnob(spec),
                _ => throw new ArgumentException(
                    $"Unknown control kind '{spec.Kind}' — expected slider, panel, toggle, valuelist, button, mdslider, colour, or knob.",
                    nameof(spec)),
            };
            if (spec.NickName is { Length: > 0 } nick) control.NickName = nick;

            // A stale placement anchor surfaces as the usual NOT_FOUND (registry attached)
            // instead of silently dropping the control somewhere else on the canvas.
            var anchor = spec.NearId is { } nearId ? Find(doc, nearId) : null;
            control.CreateAttributes();
            if (control.Attributes is { } attrs)
                attrs.Pivot = ResolveControlPivot(doc, spec, anchor, control);

            var record = new GH_UndoRecord("Wireify create control");
            record.AddAction(new GH_AddObjectAction(control));
            doc.AddObject(control, false);
            LayOut(control);
            // The placement used the control's PROBED width; its real layout can come out
            // wider (round-12 S12.10: 160 probed, 180 laid out, two thirds of the gap gone).
            // Pin the right edge where the slot put it, now that the object is really laid out.
            if (anchor?.Attributes is { Bounds: { Width: > 0 } anchorBounds } && control.Attributes is { } placed)
                PinRightEdge(control, placed, anchorBounds.Left - 30f);
            doc.UndoServer.PushUndoRecord(record);
            RecordTouch(doc, control.InstanceGuid);

            if (control is IGH_ActiveObject active) active.ExpireSolution(false);
            doc.NewSolution(false);
            return DescribeControl(doc, control.InstanceGuid);
        }

        /// <summary>Where a new control lands (round-7 item 3, corrected by round-7 finding 4).
        /// Anchored: a column left of the anchor with the control's RIGHT edge a gap clear of the
        /// component — with <c>NearInput</c>, vertically centred on that input's own grip (the
        /// Extract Parameter convention) and settled FLUSH against its neighbours; either way the
        /// spot slides DOWN past whatever already occupies it (bare <c>NearId</c> keeps the
        /// stacking gap), so four controls for four inputs form a clean column, never a pile.
        /// Unanchored: the legacy cascade start, same collision slide. The convert swap never
        /// routes here — it must land exactly on the socket's pivot.
        /// <para>The pivot is NOT the rectangle's centre: the special-object attribute classes
        /// (slider, panel, toggle, list, button, MD slider, swatch) keep it at the top-left, so
        /// assuming a centre pivot landed every control half a width right and half a height low
        /// of its target — inside the component, over its labels — and the next control's
        /// collision check then slid below the shifted one (the doubled stride). The offset is
        /// MEASURED per object instead of assumed per kind.</para></summary>
        System.Drawing.PointF ResolveControlPivot(
            GH_Document doc, ControlSpec spec, IGH_DocumentObject? anchor, IGH_DocumentObject control)
        {
            const float gap = 30f;
            var (size, pivotOffset) = Footprint(control);
            PlacementRect desired;
            var rowAnchored = false;
            if (anchor?.Attributes is { } near)
            {
                var left = near.Bounds.Width > 0 ? near.Bounds.Left : near.Pivot.X;
                var rowY = near.Bounds.Height > 0 ? near.Bounds.Y + near.Bounds.Height / 2f : near.Pivot.Y;
                if (spec.NearInput is { Length: > 0 } inputName
                    && anchor is IGH_Component comp
                    && comp.Params.Input.FirstOrDefault(
                        p => string.Equals(ParamKey(p), inputName, StringComparison.OrdinalIgnoreCase))
                        is { Attributes: { } paramAttrs })
                {
                    rowY = paramAttrs.InputGrip.Y;
                    rowAnchored = true;
                }
                desired = CanvasPlacement.ColumnSlot(left, rowY, size.Width, size.Height, gap);
            }
            else
            {
                desired = new PlacementRect(50f + doc.ObjectCount * 25f, 50f, size.Width, size.Height);
            }

            var occupied = new List<PlacementRect>();
            foreach (var obj in doc.Objects)
                if (obj.Attributes is { Bounds: { Width: > 0 } b })
                    occupied.Add(new PlacementRect(b.X, b.Y, b.Width, b.Height));
            // Row-anchored controls sit FLUSH: the input pitch (20 px) equals a slider's height,
            // so any de-collision gap pushes the control on the next input off its row and the
            // error accumulates down the column (+12 px per row, round-8 F2). Extract Parameter's
            // own layout touches too. The gap stays for bare-nearId stacking.
            var (x, yTop) = CanvasPlacement.Settle(
                desired, occupied, margin: rowAnchored ? 0f : CanvasPlacement.StackMargin);
            var (px, py) = CanvasPlacement.PivotFor(x, yTop, pivotOffset.X, pivotOffset.Y);
            return new System.Drawing.PointF(px, py);
        }

        /// <summary>A control's canvas footprint before it is added: its laid-out size and where
        /// its pivot sits inside that rectangle (bounds top-left minus pivot), measured by
        /// laying the attributes out at a probe pivot. Falls back to a nominal size per kind
        /// with a top-left pivot — the convention every special-object attribute class uses —
        /// when an attribute class refuses to lay out before it has a document.</summary>
        static (System.Drawing.SizeF Size, System.Drawing.PointF PivotOffset) Footprint(IGH_DocumentObject control)
        {
            try
            {
                if (control.Attributes is { } attrs)
                {
                    var probe = new System.Drawing.PointF(1000f, 1000f);
                    attrs.Pivot = probe;
                    attrs.ExpireLayout();
                    attrs.PerformLayout();
                    var b = attrs.Bounds;
                    if (b.Width > 1 && b.Height > 1)
                        return (b.Size, new System.Drawing.PointF(b.X - probe.X, b.Y - probe.Y));
                }
            }
            catch { /* fall through to nominal */ }
            var nominal = control switch
            {
                Grasshopper.Kernel.Special.GH_NumberSlider => new System.Drawing.SizeF(180f, 20f),
                Grasshopper.Kernel.Special.GH_Panel => new System.Drawing.SizeF(150f, 100f),
                Grasshopper.Kernel.Special.GH_MultiDimensionalSlider => new System.Drawing.SizeF(150f, 150f),
                Grasshopper.Kernel.Special.GH_ValueList => new System.Drawing.SizeF(120f, 22f),
                Grasshopper.Kernel.Special.GH_DialKnob => new System.Drawing.SizeF(90f, 90f),
                _ => new System.Drawing.SizeF(90f, 22f),
            };
            return (nominal, new System.Drawing.PointF(0f, 0f));
        }

        static Grasshopper.Kernel.Special.GH_NumberSlider BuildSlider(ControlSpec spec)
        {
            var accuracy = (spec.Accuracy ?? "float").Trim().ToLowerInvariant() switch
            {
                "float" => Grasshopper.GUI.Base.GH_SliderAccuracy.Float,
                "integer" => Grasshopper.GUI.Base.GH_SliderAccuracy.Integer,
                "even" => Grasshopper.GUI.Base.GH_SliderAccuracy.Even,
                "odd" => Grasshopper.GUI.Base.GH_SliderAccuracy.Odd,
                _ => throw new ArgumentException(
                    $"Unknown slider accuracy '{spec.Accuracy}' — float, integer, even, or odd.", nameof(spec)),
            };
            var min = spec.Min ?? 0;
            var max = spec.Max ?? 10;
            if (max < min)
                throw new ArgumentException($"Slider min ({min}) must not exceed max ({max}).", nameof(spec));

            var slider = new Grasshopper.Kernel.Special.GH_NumberSlider();
            slider.Slider.Type = accuracy;
            slider.Slider.DecimalPlaces = accuracy == Grasshopper.GUI.Base.GH_SliderAccuracy.Float
                ? Math.Max(0, Math.Min(12, spec.Decimals ?? 3))
                : 0;
            // Max, then min, then max again: either setter may clamp against the slider's
            // current domain, so a single ordering cannot land every target domain.
            slider.Slider.Maximum = (decimal)max;
            slider.Slider.Minimum = (decimal)min;
            slider.Slider.Maximum = (decimal)max;
            // Quiet set here too — no document is attached yet, but the ValueChanged path
            // should simply never fire from Wireify code.
            slider.SetSliderValue((decimal)SliderSnap.Snap(
                min, max, AccuracyOf(accuracy), slider.Slider.DecimalPlaces, spec.Value ?? min));
            return slider;
        }

        static Grasshopper.Kernel.Special.GH_MultiDimensionalSlider BuildMdSlider(ControlSpec spec)
        {
            var xmin = spec.XMin ?? 0;
            var xmax = spec.XMax ?? 1;
            var ymin = spec.YMin ?? 0;
            var ymax = spec.YMax ?? 1;
            if (xmax < xmin)
                throw new ArgumentException($"MD slider xMin ({xmin}) must not exceed xMax ({xmax}).", nameof(spec));
            if (ymax < ymin)
                throw new ArgumentException($"MD slider yMin ({ymin}) must not exceed yMax ({ymax}).", nameof(spec));

            var md = new Grasshopper.Kernel.Special.GH_MultiDimensionalSlider
            {
                SliderMode = Grasshopper.Kernel.Special.GH_MDSliderMode._2d,
                XInterval = new Rhino.Geometry.Interval(xmin, xmax),
                YInterval = new Rhino.Geometry.Interval(ymin, ymax),
            };
            // Value stores normalized fractions (X reads back as T0 + Value.X * span) — the
            // spec's x/y are domain values, clamped then normalized on the way in.
            md.Value = new Rhino.Geometry.Point3d(
                MdSliderPush.Normalize(MdSliderPush.Clamp(spec.X ?? xmin, xmin, xmax), xmin, xmax),
                MdSliderPush.Normalize(MdSliderPush.Clamp(spec.Y ?? ymin, ymin, ymax), ymin, ymax),
                0);
            return md;
        }

        static Grasshopper.Kernel.Special.GH_ColourSwatch BuildColourSwatch(ControlSpec spec)
        {
            var hex = spec.Colour ?? "#ffffff";
            if (!ColourHex.TryParse(hex, out var a, out var r, out var g, out var b))
                throw new ArgumentException(
                    $"Colour '{hex}' is not a hex colour — #RGB, #RRGGBB, or #RRGGBBAA.", nameof(spec));
            return new Grasshopper.Kernel.Special.GH_ColourSwatch
            {
                SwatchColour = System.Drawing.Color.FromArgb(a, r, g, b),
            };
        }

        static Grasshopper.Kernel.Special.GH_DialKnob BuildKnob(ControlSpec spec)
        {
            var min = spec.Min ?? 0;
            var max = spec.Max ?? 10;
            if (max < min)
                throw new ArgumentException($"Knob min ({min}) must not exceed max ({max}).", nameof(spec));

            var knob = new Grasshopper.Kernel.Special.GH_DialKnob
            {
                Decimals = Math.Max(0, Math.Min(12, spec.Decimals ?? 3)),
            };
            // Max, then min, then max again — the slider's setter-clamp lesson applied to the
            // knob's decimal domain properties.
            knob.Maximum = (decimal)max;
            knob.Minimum = (decimal)min;
            knob.Maximum = (decimal)max;
            knob.Value = (decimal)SliderSnap.Snap(min, max, SliderAccuracy.Float, knob.Decimals, spec.Value ?? min);
            return knob;
        }

        static Grasshopper.Kernel.Special.GH_ValueList BuildValueList(ControlSpec spec)
        {
            if (spec.Items is not { Count: > 0 })
                throw new ArgumentException(
                    "A valuelist needs items — [{name, expression}, ...] in order.", nameof(spec));
            var list = new Grasshopper.Kernel.Special.GH_ValueList();
            list.ListItems.Clear(); // GH seeds placeholder items
            foreach (var item in spec.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Expression))
                    throw new ArgumentException(
                        "Every valuelist item needs a name and an expression (e.g. 3 or \"steel\").", nameof(spec));
                list.ListItems.Add(new Grasshopper.Kernel.Special.GH_ValueListItem(item.Name, item.Expression));
            }
            var selected = spec.Selected ?? 0;
            if (selected < 0 || selected >= list.ListItems.Count)
                throw new ArgumentException(
                    $"Selected index {selected} is out of range for {list.ListItems.Count} item(s).", nameof(spec));
            list.SelectItem(selected);
            return list;
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

                // Wires migrate from the STAGED name to the FINAL name — a renamed input
                // (in1 -> lines) keeps its wire; the receipt reports the final names, which
                // are what the component actually carries.
                var finalByStaged = io.Inputs.ToDictionary(
                    s => s.Name, s => s.RenameTo ?? s.Name, StringComparer.OrdinalIgnoreCase);
                var wired = new List<string>();
                foreach (var (name, sources) in staged)
                {
                    if (sources.Count == 0) continue;
                    var final = finalByStaged.TryGetValue(name, out var renamed) ? renamed : name;
                    var target = newComp.Params.Input.First(
                        p => string.Equals(ParamKey(p), final, StringComparison.OrdinalIgnoreCase));
                    record.AddAction(new GH_WireAction(target));
                    foreach (var source in sources) target.AddSource(source);
                    wired.Add(final);
                }

                foreach (var param in socket.Params.Input)
                    record.AddAction(new GH_WireAction(param));
                record.AddAction(new GH_RemoveObjectAction(socket));
                doc.RemoveObject(socket, false);

                LayOut(newComp);

                doc.UndoServer.PushUndoRecord(record);

                // Expire without an immediate recompute, then one NewSolution — expire(true)
                // solves eagerly and the NewSolution below solves again, and every extra solve
                // costs real time on a heavy canvas (and pushes a spurious app frame).
                if (newComp is IGH_ActiveObject active) active.ExpireSolution(false);
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
            var doc = Doc(mutate: true);
            var comp = AsComponent(Find(doc, id));

            var inputSpecs = inputs ?? Array.Empty<IoParamSpec>();
            var io = StagedConversion.ValidateIo(
                inputSpecs.Select(i => i.Name).ToList(), inputSpecs, outputs);
            if (io.Error is not null) throw new ArgumentException(io.Error);

            // Preserve wires on inputs whose name survives the redefinition — and across an
            // explicit rename: a spec's Name addresses the CURRENT param, RenameTo is what the
            // rebuilt param answers to, and the wire follows (round-7 item 1: renaming an input
            // no longer costs its wire).
            var oldSources = comp.Params.Input.ToDictionary(
                ParamKey, p => p.Sources.ToList(), StringComparer.OrdinalIgnoreCase);
            var sourcesByFinal = new Dictionary<string, List<IGH_Param>>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in io.Inputs)
                if (oldSources.TryGetValue(spec.Name, out var carried))
                    sourcesByFinal[spec.RenameTo ?? spec.Name] = carried;

            BuildScriptIo(comp, io.Inputs, io.Outputs);

            foreach (var param in comp.Params.Input)
                if (sourcesByFinal.TryGetValue(ParamKey(param), out var sources))
                    foreach (var source in sources)
                        param.AddSource(source);

            RecordTouch(doc, id);
            if (comp is IGH_ActiveObject active) active.ExpireSolution(true);
            // The raw selected-hint read is best-effort against a private registry — the specs
            // just applied are authoritative for the echo (folded to their FINAL names, which
            // are what the introspection reports).
            return HintSelection.ApplyDeclaredHints(Introspect(comp), Finalized(io.Inputs), io.Outputs);
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
                // RenameTo is the final variable name; Name stayed the match key so callers
                // could address the staged/current param while renaming it (round-7 item 1).
                var param = RhinoCodeInterop.CreateScriptVariableParam(
                    comp, spec.RenameTo ?? spec.Name, AccessFrom(spec.Access), optional: true, spec.TypeHint);
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

        /// <summary>Specs folded to their final names (RenameTo applied, then cleared) — for
        /// consumers that match against the BUILT params rather than the staged/current ones.</summary>
        static IReadOnlyList<IoParamSpec> Finalized(IReadOnlyList<IoParamSpec> specs)
            => specs.Select(s => s.RenameTo is null ? s : s with { Name = s.RenameTo, RenameTo = null }).ToList();

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

            // SDK-mode guard: the engine treats the RunScript signature as derived state — on
            // rebuild it rewrites the def line to match the component's CURRENT params, leaving
            // a mismatched body referencing names that no longer exist, silently. Refuse the
            // write up front with the recipe instead (round-4 defect B: the rewrite cost ~20
            // minutes and four dead ends live).
            if (ScriptSignature.TryGetParams(source, out var signatureParams)
                && obj is IGH_Component scriptComp)
            {
                var currentInputs = scriptComp.Params.Input.Select(ParamKey).ToList();
                if (!signatureParams.SequenceEqual(currentInputs, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"The source's RunScript signature ({string.Join(", ", signatureParams)}) does not match "
                        + $"component {id}'s current inputs ({string.Join(", ", currentInputs)}) — the script engine "
                        + "would silently rewrite the signature to the current inputs and orphan the body. Declare "
                        + "the params first (set_io), then set the source; or write RunScript to match the current inputs.");
            }

            if (runtime == PythonRuntime.IronPython2)
                RhinoCodeInterop.SetIronPythonCode(obj, source);
            else
                RhinoCodeInterop.SetSource(obj, EnsureDirective(source));
            RhinoCodeInterop.ReBuild(obj, _rebuildTimeout);
            RecordTouch(doc, id);

            // Solve-and-report by default: a revision's outputs are read fresh in the same call,
            // never stale (the round-4 lesson, made mechanical). Expire WITHOUT recompute, then
            // one NewSolution — expire(true) + NewSolution ran every revision's solve twice.
            if (!solve) return null;
            if (obj is IGH_ActiveObject active) active.ExpireSolution(false);
            doc.NewSolution(false);
            return BuildReport(doc, obj, includeDocument: false);
        }

        public TypedIoResult SetParametersFromScript(Guid id)
        {
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            var before = InputNamesOf(obj);
            RhinoCodeInterop.SetParametersFromScript(obj);
            RecordTouch(doc, id);
            var after = InputNamesOf(obj);
            return new TypedIoResult(id, !before.SequenceEqual(after, StringComparer.Ordinal), before, after);
        }

        static IReadOnlyList<string> InputNamesOf(IGH_DocumentObject obj)
            => obj is IGH_Component comp
                ? comp.Params.Input.Select(ParamKey).ToList()
                : Array.Empty<string>();

        public DeletedComponent DeleteComponent(Guid id)
        {
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            var deletable =
                (obj is IGH_Component socket && socket.ComponentGuid == WireifyIds.SocketComponentGuid)
                || RhinoCodeInterop.TryGetSource(obj, out _)
                || IsAppControl(obj);
            if (!deletable)
                throw new InvalidOperationException(
                    $"Object {id} ('{obj.Name}', nickname '{obj.NickName}') is not Wireify-managed — only Wireify "
                    + "sockets, script components, and native app controls (slider, panel, toggle, value list, "
                    + "button, MD slider, colour swatch, dial knob) can be deleted here. Remove anything else "
                    + "manually in Grasshopper.");

            var name = obj.Name ?? "";
            var nick = obj.NickName ?? "";

            // One undo record, wires included: snapshot every affected param before the removal so
            // ctrl-Z restores the object with its connections (the convert_staged pattern). A
            // floating param (every app control) has recipients of its own instead of output params.
            var record = new GH_UndoRecord("Wireify delete");
            if (obj is IGH_Component comp)
            {
                foreach (var p in comp.Params.Input)
                    record.AddAction(new GH_WireAction(p));
                foreach (var op in comp.Params.Output)
                    foreach (var recipient in op.Recipients.ToList())
                        record.AddAction(new GH_WireAction(recipient));
            }
            else if (obj is IGH_Param floating)
            {
                foreach (var recipient in floating.Recipients.ToList())
                    record.AddAction(new GH_WireAction(recipient));
            }
            record.AddAction(new GH_RemoveObjectAction(obj));

            doc.RemoveObject(obj, false);
            doc.UndoServer.PushUndoRecord(record);
            doc.NewSolution(false);

            return new DeletedComponent(id, name, nick);
        }

        static bool IsAppControl(IGH_DocumentObject obj) => obj
            is Grasshopper.Kernel.Special.GH_NumberSlider
            or Grasshopper.Kernel.Special.GH_Panel
            or Grasshopper.Kernel.Special.GH_BooleanToggle
            or Grasshopper.Kernel.Special.GH_ValueList
            or Grasshopper.Kernel.Special.GH_ButtonObject
            or Grasshopper.Kernel.Special.GH_MultiDimensionalSlider
            or Grasshopper.Kernel.Special.GH_ColourSwatch
            or Grasshopper.Kernel.Special.GH_DialKnob;

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
            // A floating source (a slider, a panel) re-fits its capsule once it is wired, and
            // grew rightward into the gap it was placed with (round-12 S12.10) — the same edge
            // rule a rename keeps applies after the wire lands.
            var sourceTop = source.Attributes?.GetTopLevel?.DocObject as IGH_Param;
            var sourceRect = sourceTop?.Attributes?.Bounds ?? System.Drawing.RectangleF.Empty;
            target.AddSource(source);
            doc.UndoServer.PushUndoRecord(record);

            // Interactive wiring recomputes the canvas; the tool matches that, so a read right
            // after a wire sees live data instead of an empty pre-solve preview (round-17 finding:
            // volatile data stays empty until a solve — re-reading alone never refreshes it).
            target.ExpireSolution(false);
            doc.NewSolution(false);

            if (sourceTop is not null && sourceRect.Width > 0)
            {
                LayOut(sourceTop);
                KeepAnchoredEdge(sourceTop, sourceRect);
            }

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

            WritePanelText(doc, panel, text);
            // Tool path only: an app-surface panel push shares WritePanelText but is the user's
            // own gesture, never agent authorship — it must not badge the panel.
            RecordTouch(doc, id);
            return new PanelText(id, panel.NickName ?? "", text.Length);
        }

        /// <summary>The one panel-write implementation — set_panel_text and the app surface's
        /// panel pushes share it, so the undo record and solve semantics can never drift.
        /// Expire(false) + one NewSolution: expire(true) solved eagerly and the NewSolution
        /// solved again — two SolutionEnds per write, seen live as a doubled app counter.</summary>
        static void WritePanelText(GH_Document doc, Grasshopper.Kernel.Special.GH_Panel panel, string text)
        {
            var record = new GH_UndoRecord("Wireify set panel text");
            record.AddAction(new GH_GenericObjectAction(panel));
            panel.UserText = text;
            doc.UndoServer.PushUndoRecord(record);
            panel.ExpireSolution(false);
            doc.NewSolution(false);
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

        // --- Companion app (webapp surface) --------------------------------------------------

        public AppState ReadAppState(AppQuery query) => BuildAppState(Doc(), query, _docs);

        // A browser drag arrives as a stream of sets; one undo record per set would flood the
        // user's stack. Explicit gesture brackets from the client are the primary coalescing
        // mechanism (a slow human drag's ~800 ms steps defeat any time gap — the round-4
        // finding); the quiet-gap fallback, measured from the previous push's COMPLETION so a
        // long solve never counts as user silence, covers pages that send no markers. Discrete
        // controls (toggle, value list) record per push. UI-thread only (every mutation runs
        // marshalled + serialized), so the coalescer's plain dictionaries are safe.
        static readonly TimeSpan AppGestureGap = TimeSpan.FromMilliseconds(750);
        readonly GestureCoalescer _appGestures = new(AppGestureGap);

        public AppControlState SetAppGesture(Guid id, bool open)
        {
            _appGestures.SetGesture(id, open);
            // A marker answers the same envelope as a value push (round-5 S5.1m): the fresh
            // control state is a cheap single-object read — no views, no solve, no mutation.
            return DescribeControl(Doc(), id);
        }

        public AppSetResult SetAppControlValue(Guid id, AppPushValue value)
        {
            // A mutation like any other — the front-tab gate applies. Round 7 exempted this
            // path for one build and measured what that bought: the value LANDED on the
            // background document but nothing downstream SOLVED until the tab was fronted
            // (Grasshopper solves only the front document), so the page showed a new input
            // beside empty outputs under a pill that read live (finding 1). The refusal is the
            // honest answer; the page renders it on the pill and on the control.
            var doc = Doc(mutate: true);
            var obj = Find(doc, id);
            switch (obj)
            {
                case Grasshopper.Kernel.Special.GH_NumberSlider slider:
                {
                    if (value.Number is not { } number)
                        throw new InvalidOperationException(
                            $"Control {id} ('{slider.NickName}') is a Number Slider — push a numeric value.");
                    // Snap onto the slider's own grid (its accuracy + decimals), not just its
                    // domain — an integer/even/odd slider must never be handed an off-grid
                    // value, whatever the browser control produced.
                    var applied = SliderSnap.Snap(
                        (double)slider.Slider.Minimum, (double)slider.Slider.Maximum,
                        AccuracyOf(slider.Slider.Type), slider.Slider.DecimalPlaces, number);
                    // A push that lands on the value already held is a full no-op: no undo
                    // record, no solve, no stream frame — just the honest receipt.
                    if (applied == (double)slider.Slider.Value)
                        return new AppSetResult(DescribeControl(doc, id), applied != number);
                    RecordAppUndo(doc, slider, coalesce: true);
                    // SetSliderValue, never the inner Slider.Value setter: the inner setter
                    // raises ValueChanged, and GH_NumberSlider's own handler treats each
                    // non-intermediate change as a committed edit — its OWN undo record per
                    // distinct value plus its own ExpireSolution(true) solve (the round-3
                    // one-undo-per-integer-step bug). The quiet set leaves our coalesced
                    // record as the only undo and the explicit solve below as the only solve.
                    slider.SetSliderValue((decimal)applied);
                    slider.ExpireSolution(false);
                    doc.NewSolution(false);
                    _appGestures.MarkCompleted(id);
                    return new AppSetResult(DescribeControl(doc, id), applied != number);
                }
                case Grasshopper.Kernel.Special.GH_BooleanToggle toggle:
                {
                    if (value.Flag is not { } flag)
                        throw new InvalidOperationException(
                            $"Control {id} ('{toggle.NickName}') is a Boolean Toggle — push true or false.");
                    RecordAppUndo(doc, toggle, coalesce: false);
                    toggle.Value = flag;
                    toggle.ExpireSolution(false);
                    doc.NewSolution(false);
                    return new AppSetResult(DescribeControl(doc, id), false);
                }
                case Grasshopper.Kernel.Special.GH_ValueList list:
                {
                    var index = ResolveValueListIndex(id, list, value);
                    RecordAppUndo(doc, list, coalesce: false);
                    list.SelectItem(index);
                    list.ExpireSolution(false);
                    doc.NewSolution(false);
                    return new AppSetResult(DescribeControl(doc, id), false);
                }
                case Grasshopper.Kernel.Special.GH_Panel panel:
                {
                    if (value.Text is not { } text)
                        throw new InvalidOperationException(
                            $"Control {id} ('{panel.NickName}') is a Panel — push a text value.");
                    // Same implementation as set_panel_text, its undo record included.
                    WritePanelText(doc, panel, text);
                    return new AppSetResult(DescribeControl(doc, id), false);
                }
                case Grasshopper.Kernel.Special.GH_ButtonObject button:
                {
                    if (value.Flag is not { } pressed)
                        throw new InvalidOperationException(
                            $"Control {id} ('{button.NickName}') is a Button — push true (press) or false (release).");
                    // Momentary by nature: no undo record (a button press is not persisted
                    // state), and the app mirrors the hold — true while pressed, false on release.
                    button.ButtonDown = pressed;
                    button.ExpireSolution(false);
                    doc.NewSolution(false);
                    return new AppSetResult(DescribeControl(doc, id), false);
                }
                case Grasshopper.Kernel.Special.GH_MultiDimensionalSlider md:
                {
                    if (value.Text is not { } mdText || !MdSliderPush.TryParse(mdText, out var parts))
                        throw new InvalidOperationException(
                            $"Control {id} ('{md.NickName}') is an MD Slider — push comma-separated axis values, e.g. \"0.25,0.8\".");
                    var axisCount = md.SliderMode == Grasshopper.Kernel.Special.GH_MDSliderMode._3d ? 3 : 2;
                    if (parts.Count != axisCount)
                        throw new InvalidOperationException(
                            $"Control {id} ('{md.NickName}') is a {axisCount}-axis MD Slider — push exactly {axisCount} comma-separated values.");
                    var ax = MdSliderPush.Clamp(parts[0], md.XInterval.T0, md.XInterval.T1);
                    var ay = MdSliderPush.Clamp(parts[1], md.YInterval.T0, md.YInterval.T1);
                    var az = axisCount == 3 ? MdSliderPush.Clamp(parts[2], md.ZInterval.T0, md.ZInterval.T1) : md.Z;
                    var mdClamped = ax != parts[0] || ay != parts[1] || (axisCount == 3 && az != parts[2]);
                    if (ax == md.X && ay == md.Y && (axisCount != 3 || az == md.Z))
                        return new AppSetResult(DescribeControl(doc, id), mdClamped);
                    RecordAppUndo(doc, md, coalesce: true);
                    // Same law as the slider: our explicit expire + solve must stay the only
                    // solve and our coalesced record the only undo. The Point3d setter has no
                    // documented internal machinery, but only a PC round can prove that — the
                    // round-4 counter/ctrl-Z watch covers it.
                    // Value stores normalized fractions of each interval, NOT domain values —
                    // the round-4 defect: writing 40 straight in parked a 0–100 axis at 4000
                    // (X reads back as T0 + Value.X * span). Everything above this line speaks
                    // domain; only the store is normalized.
                    md.Value = new Rhino.Geometry.Point3d(
                        MdSliderPush.Normalize(ax, md.XInterval.T0, md.XInterval.T1),
                        MdSliderPush.Normalize(ay, md.YInterval.T0, md.YInterval.T1),
                        axisCount == 3 ? MdSliderPush.Normalize(az, md.ZInterval.T0, md.ZInterval.T1) : 0);
                    md.ExpireSolution(false);
                    doc.NewSolution(false);
                    _appGestures.MarkCompleted(id);
                    return new AppSetResult(DescribeControl(doc, id), mdClamped);
                }
                case Grasshopper.Kernel.Special.GH_ColourSwatch swatch:
                {
                    if (value.Text is not { } hexText || !ColourHex.TryParse(hexText, out var ca, out var cr, out var cg, out var cb))
                        throw new InvalidOperationException(
                            $"Control {id} ('{swatch.NickName}') is a Colour Swatch — push a hex colour: #RGB, #RRGGBB, or #RRGGBBAA.");
                    var next = System.Drawing.Color.FromArgb(ca, cr, cg, cb);
                    if (swatch.SwatchColour.ToArgb() == next.ToArgb())
                        return new AppSetResult(DescribeControl(doc, id), false);
                    // A colour pick is a deliberate commit (the binder pushes on change, not
                    // per picker drag) — one undo record per push, like the toggle.
                    RecordAppUndo(doc, swatch, coalesce: false);
                    swatch.SwatchColour = next;
                    swatch.ExpireSolution(false);
                    doc.NewSolution(false);
                    return new AppSetResult(DescribeControl(doc, id), false);
                }
                case Grasshopper.Kernel.Special.GH_DialKnob knob:
                {
                    if (value.Number is not { } knobNumber)
                        throw new InvalidOperationException(
                            $"Control {id} ('{knob.NickName}') is a Dial Knob — push a numeric value.");
                    // The knob has no accuracy enum — its grid is decimals-only, which is
                    // exactly SliderSnap's float path.
                    var knobApplied = SliderSnap.Snap(
                        (double)knob.Minimum, (double)knob.Maximum,
                        SliderAccuracy.Float, knob.Decimals, knobNumber);
                    if (knobApplied == (double)knob.Value)
                        return new AppSetResult(DescribeControl(doc, id), knobApplied != knobNumber);
                    RecordAppUndo(doc, knob, coalesce: true);
                    knob.Value = (decimal)knobApplied;
                    knob.ExpireSolution(false);
                    doc.NewSolution(false);
                    _appGestures.MarkCompleted(id);
                    return new AppSetResult(DescribeControl(doc, id), knobApplied != knobNumber);
                }
                default:
                    throw new InvalidOperationException(
                        $"Object {id} ('{obj.Name}', nickname '{obj.NickName}') is not a supported app control — "
                        + "number sliders, MD sliders, dial knobs, colour swatches, panels, boolean toggles, "
                        + "value lists, and buttons take pushes.");
            }
        }

        static int ResolveValueListIndex(Guid id, Grasshopper.Kernel.Special.GH_ValueList list, AppPushValue value)
        {
            int index;
            if (value.Number is { } number)
            {
                index = (int)Math.Round(number);
            }
            else if (value.Text is { } name)
            {
                index = -1;
                for (var i = 0; i < list.ListItems.Count; i++)
                {
                    if (string.Equals(list.ListItems[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    throw new InvalidOperationException(
                        $"Control {id} ('{list.NickName}') has no item named '{name}'.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Control {id} ('{list.NickName}') is a Value List — push an item index or an item name.");
            }

            if (index < 0 || index >= list.ListItems.Count)
                throw new InvalidOperationException(
                    $"Control {id} ('{list.NickName}') has {list.ListItems.Count} item(s) — index {index} is out of range.");
            return index;
        }

        void RecordAppUndo(GH_Document doc, IGH_DocumentObject obj, bool coalesce)
        {
            if (coalesce && !_appGestures.IsNewGesture(obj.InstanceGuid)) return;
            var record = new GH_UndoRecord("Wireify app push");
            record.AddAction(new GH_GenericObjectAction(obj));
            doc.UndoServer.PushUndoRecord(record);
        }

        static SliderAccuracy AccuracyOf(Grasshopper.GUI.Base.GH_SliderAccuracy type) => type switch
        {
            Grasshopper.GUI.Base.GH_SliderAccuracy.Integer => SliderAccuracy.Integer,
            Grasshopper.GUI.Base.GH_SliderAccuracy.Even => SliderAccuracy.Even,
            Grasshopper.GUI.Base.GH_SliderAccuracy.Odd => SliderAccuracy.Odd,
            _ => SliderAccuracy.Float,
        };

        public IDisposable SubscribeSolutionEnd(
            Func<AppQuery?> queryProvider, Action<AppState> onSolution, Action<string>? onClosed = null,
            Action? onSolveStart = null, Action<AppState>? onActiveChanged = null)
        {
            if (queryProvider is null) throw new ArgumentNullException(nameof(queryProvider));
            if (onSolution is null) throw new ArgumentNullException(nameof(onSolution));
            var doc = Doc(); // read path: subscribing from a background tab is fine
            // The feed keeps the CALLER's routing context (the app URL's home, path-first) so it
            // can re-resolve its document later, on the UI thread, outside any marshalled call.
            return new SolutionFeed(
                _docs, _docs.CurrentContext, doc, queryProvider, onSolution, onClosed, onSolveStart, onActiveChanged);
        }

        /// <summary>
        /// One home's live feed. The document it listens to is re-resolved from the home whenever
        /// the open documents change (a document added or removed, the attached one saved under
        /// a new path) and before every frame — never captured once for the life of the stream.
        /// Round 10 (S10.7) measured what the capture cost: after a Save As the attached instance
        /// WAS the renamed copy, so the SSE frames carried the copy's values, name and
        /// isActiveCanvas while the state GET and the geometry read the reopened original, and
        /// the page rendered two files at once with the pill inverted. The rule is
        /// <see cref="DocRouting.DecideFeed"/>: same document, keep; another one resolves, move
        /// the handlers and push a fresh frame; nothing resolves, end the feed honestly (the page
        /// polls and heals when the file is back).
        ///
        /// Every handler fires ON the UI thread: snapshot (cheap, capped reads) and hand off —
        /// never back through the marshalling gate, which may be held by the very call that
        /// triggered the event. The query is re-read per event so a manifest edit is live on the
        /// next recompute; null = manifest unreadable right now — skip.
        /// </summary>
        sealed class SolutionFeed : IDisposable
        {
            readonly SessionDocumentResolver _docs;
            readonly SessionCallContext? _context;
            readonly Func<AppQuery?> _query;
            readonly Action<AppState> _onSolution;
            readonly Action<string>? _onClosed;
            readonly Action? _onSolveStart;
            readonly Action<AppState>? _onActiveChanged;
            readonly GH_DocumentServer? _server;
            readonly GH_Document.SolutionEndEventHandler _endHandler;
            readonly GH_Document.SolutionStartEventHandler _startHandler;
            readonly GH_Document.ContextChangedEventHandler _contextHandler;
            readonly GH_Document.FilePathChangedEventHandler _pathHandler;
            readonly GH_DocumentServer.DocumentAddedEventHandler _addedHandler;
            readonly GH_DocumentServer.DocumentRemovedEventHandler _removedHandler;
            GH_Document? _attached;
            string? _attachedName;
            int _disposed;

            public SolutionFeed(
                SessionDocumentResolver docs, SessionCallContext? context, GH_Document doc,
                Func<AppQuery?> query, Action<AppState> onSolution, Action<string>? onClosed,
                Action? onSolveStart, Action<AppState>? onActiveChanged)
            {
                _docs = docs;
                _context = context;
                _query = query;
                _onSolution = onSolution;
                _onClosed = onClosed;
                _onSolveStart = onSolveStart;
                _onActiveChanged = onActiveChanged;
                _server = Grasshopper.Instances.DocumentServer;

                _endHandler = (sender, e) =>
                {
                    // The guard: a frame is built only on the document the home resolves to NOW.
                    if (!ReferenceEquals(_docs.TryResolve(_context), _attached)) { Rebind(); return; }
                    Snapshot(_onSolution);
                };
                // SolutionStart drives the honest stale pill: the solve is in flight, the state on
                // screen is about to be outdated, and pushes will queue. No snapshot here —
                // mid-solve reads are neither cheap nor safe; the subscriber sends a lightweight
                // status marker only.
                _startHandler = (sender, e) =>
                {
                    if (_onSolveStart is null) return;
                    try { _onSolveStart(); }
                    catch { /* subscriber failures never break the solve */ }
                };
                // Tab switches fire ContextChanged (Loaded = front tab, Unloaded = backgrounded)
                // and no solve — without this no frame marks the change and the pill claims live
                // while pushes refuse with DOC_NOT_ACTIVE (round-4 finding). The event context IS
                // the new truth — never read the global here: it has not flipped yet when this
                // fires (S5.5a). Unloaded is deferred one idle pass: a CLOSING document unloads
                // first and is removed after, and the immediate frame told the page "not in
                // front" about a file that was gone a millisecond later (round-10 S10.3).
                _contextHandler = (sender, e) =>
                {
                    if (_onActiveChanged is null) return;
                    if (e.Context == GH_DocumentContext.Loaded)
                    {
                        Snapshot(_onActiveChanged, isActiveOverride: true);
                        return;
                    }
                    if (e.Context != GH_DocumentContext.Unloaded) return;
                    var unloaded = _attached;
                    Defer(() =>
                    {
                        if (unloaded is null || !ReferenceEquals(_attached, unloaded)) return;
                        if (!SessionDocumentResolver.IsOpen(unloaded)) return;
                        Snapshot(_onActiveChanged, isActiveOverride: false);
                    });
                };
                // Save As moves the attached instance to another path: the home's file is no
                // longer open, so the feed ends (B69's plate line says where the app stayed) —
                // or, when the original is already open again, switches to it.
                _pathHandler = (sender, e) => Rebind();
                _addedHandler = (sender, added) => Rebind();
                // Closing the definition removes it from the document server; without this the
                // stream just goes silent. Closing some OTHER document is nothing to the feed.
                _removedHandler = (sender, removed) =>
                {
                    if (ReferenceEquals(removed, _attached)) Rebind();
                };

                Attach(doc);
                if (_server is not null)
                {
                    _server.DocumentAdded += _addedHandler;
                    _server.DocumentRemoved += _removedHandler;
                }
            }

            void Attach(GH_Document doc)
            {
                _attached = doc;
                _attachedName = SessionDocumentResolver.FileNameOf(doc);
                doc.SolutionEnd += _endHandler;
                doc.SolutionStart += _startHandler;
                doc.ContextChanged += _contextHandler;
                doc.FilePathChanged += _pathHandler;
            }

            void Detach()
            {
                if (_attached is not { } doc) return;
                doc.SolutionEnd -= _endHandler;
                doc.SolutionStart -= _startHandler;
                doc.ContextChanged -= _contextHandler;
                doc.FilePathChanged -= _pathHandler;
                _attached = null;
            }

            /// <summary>Re-resolve the home's document and act on the difference.</summary>
            void Rebind()
            {
                if (_disposed != 0) return;
                var resolved = _docs.TryResolve(_context);
                switch (DocRouting.DecideFeed(ReferenceEquals(resolved, _attached), resolved is not null))
                {
                    case FeedRebind.Keep:
                        return;
                    case FeedRebind.Switch:
                        Detach();
                        Attach(resolved!);
                        Snapshot(_onSolution);
                        return;
                    default:
                        var name = _attachedName;
                        Detach();
                        try { _onClosed?.Invoke(ErrorProtocol.PageDocNotOpen(name)); }
                        catch { /* subscriber failures never break the document close */ }
                        return;
                }
            }

            void Snapshot(Action<AppState> deliver, bool? isActiveOverride = null)
            {
                if (_disposed != 0 || _attached is not { } doc) return;
                AppState snapshot;
                try
                {
                    if (_query() is not { } query) return;
                    snapshot = BuildAppState(doc, query, _docs, isActiveOverride);
                }
                catch { return; } // a torn read mid-close must never break the event
                try { deliver(snapshot); }
                catch { /* subscriber failures never break the canvas */ }
            }

            /// <summary>Run <paramref name="action"/> on the next Rhino idle pass — after the
            /// synchronous close that may have triggered the event has finished. Falls back to
            /// running inline when the idle event is unavailable.</summary>
            static void Defer(Action action)
            {
                try
                {
                    EventHandler? once = null;
                    once = (s, e) =>
                    {
                        Rhino.RhinoApp.Idle -= once;
                        try { action(); } catch { /* best-effort */ }
                    };
                    Rhino.RhinoApp.Idle += once;
                }
                catch
                {
                    try { action(); } catch { /* best-effort */ }
                }
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Detach();
                if (_server is not null)
                {
                    _server.DocumentAdded -= _addedHandler;
                    _server.DocumentRemoved -= _removedHandler;
                }
            }
        }

        /// <summary>One declared control's live state, lenient by design: a vanished control
        /// reports "missing", an unsupported kind reports its real name — the app renders the
        /// truth instead of the whole read failing. Also the receipt a push answers with.</summary>
        static AppControlState DescribeControl(GH_Document doc, Guid id)
        {
            var obj = doc.FindObject(id, true);
            return obj switch
            {
                null => new AppControlState(id, "", "missing"),
                // Value/min/max speak RAW slider values so a push reads back exactly what it
                // wrote (an expression slider's CurrentValue reads through the expression —
                // reporting it as value made pushes non-idempotent: send 10, be told 20, thumb
                // jumps to max). The evaluated number rides separately as display truth.
                Grasshopper.Kernel.Special.GH_NumberSlider slider => new AppControlState(
                    id, obj.NickName ?? "", "slider",
                    (double)slider.Slider.Value,
                    (double)slider.Slider.Minimum,
                    (double)slider.Slider.Maximum,
                    slider.Slider.DecimalPlaces,
                    Step: SliderSnap.StepFor(AccuracyOf(slider.Slider.Type), slider.Slider.DecimalPlaces),
                    Evaluated: slider.IsExpression ? (double)slider.CurrentValue : null),
                Grasshopper.Kernel.Special.GH_Panel panel => new AppControlState(
                    id, obj.NickName ?? "", "panel", Text: panel.UserText),
                Grasshopper.Kernel.Special.GH_BooleanToggle toggle => new AppControlState(
                    id, obj.NickName ?? "", "toggle", Bool: toggle.Value),
                Grasshopper.Kernel.Special.GH_ButtonObject button => new AppControlState(
                    id, obj.NickName ?? "", "button", Bool: button.ButtonDown),
                Grasshopper.Kernel.Special.GH_ValueList list => DescribeValueList(id, list),
                Grasshopper.Kernel.Special.GH_MultiDimensionalSlider md => DescribeMdSlider(id, md),
                Grasshopper.Kernel.Special.GH_ColourSwatch swatch => new AppControlState(
                    id, obj.NickName ?? "", "colour",
                    Colour: ColourHex.Format(
                        swatch.SwatchColour.A, swatch.SwatchColour.R, swatch.SwatchColour.G, swatch.SwatchColour.B)),
                Grasshopper.Kernel.Special.GH_DialKnob knob => new AppControlState(
                    id, obj.NickName ?? "", "knob",
                    (double)knob.Value,
                    (double)knob.Minimum,
                    (double)knob.Maximum,
                    knob.Decimals,
                    Step: SliderSnap.StepFor(SliderAccuracy.Float, knob.Decimals)),
                _ => new AppControlState(id, obj.NickName ?? "", $"unsupported:{obj.Name}"),
            };
        }

        static AppControlState DescribeMdSlider(Guid id, Grasshopper.Kernel.Special.GH_MultiDimensionalSlider md)
        {
            // Bounds are reported normalized (a canvas interval may be authored decreasing);
            // the pad in the browser only ever needs min <= max.
            var axes = new List<AppAxisState>
            {
                new(md.X, Math.Min(md.XInterval.T0, md.XInterval.T1), Math.Max(md.XInterval.T0, md.XInterval.T1)),
                new(md.Y, Math.Min(md.YInterval.T0, md.YInterval.T1), Math.Max(md.YInterval.T0, md.YInterval.T1)),
            };
            if (md.SliderMode == Grasshopper.Kernel.Special.GH_MDSliderMode._3d)
                axes.Add(new(md.Z, Math.Min(md.ZInterval.T0, md.ZInterval.T1), Math.Max(md.ZInterval.T0, md.ZInterval.T1)));
            return new AppControlState(id, md.NickName ?? "", "mdslider", Axes: axes);
        }

        static AppControlState DescribeValueList(Guid id, Grasshopper.Kernel.Special.GH_ValueList list)
        {
            var items = new List<string>(list.ListItems.Count);
            var selected = -1;
            for (var i = 0; i < list.ListItems.Count; i++)
            {
                items.Add(list.ListItems[i].Name ?? "");
                if (selected < 0 && list.ListItems[i].Selected) selected = i;
            }
            return new AppControlState(
                id, list.NickName ?? "", "valuelist",
                Items: items,
                Selected: selected < 0 ? null : selected,
                Text: selected < 0 ? null : items[selected]);
        }

        /// <summary>Snapshot the declared params' state off the document. Per-param lenient by
        /// design: a control that vanished reports "missing", a declared non-slider reports its
        /// real kind — the app renders the truth instead of the whole state read failing.</summary>
        static AppState BuildAppState(
            GH_Document doc, AppQuery query, SessionDocumentResolver docs, bool? isActiveOverride = null)
        {
            var controls = new List<AppControlState>();
            foreach (var id in query.Controls)
                controls.Add(DescribeControl(doc, id));

            var views = new List<InputData>();
            foreach (var view in query.Views)
                views.Add(BuildView(doc, view));

            // The override exists for the ContextChanged path: the event fires BEFORE GH's
            // global active-canvas state flips, so a snapshot reading the global reports the
            // document it is LEAVING as still active (round-5 S5.5a — one frame, pre-switch
            // value, pill lies). The event itself is the truth: Loaded = this doc fronted,
            // Unloaded = it left.
            var (units, tolerance) = DocUnitsOf();
            return new AppState(
                isActiveOverride ?? docs.IsActive(doc), controls, views, DocNameOf(doc), query.Warnings,
                units, tolerance);
        }

        /// <summary>The Rhino document's unit system and absolute tolerance — a page labels axes
        /// and a report labels its numbers from these instead of a script plumbing units
        /// through as a view (round-7 §7 E6: foot-valued geometry in a Millimeters document).</summary>
        static (string? Units, double? Tolerance) DocUnitsOf()
        {
            try
            {
                var rhinoDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rhinoDoc is null) return (null, null);
                return (rhinoDoc.ModelUnitSystem.ToString(), rhinoDoc.ModelAbsoluteTolerance);
            }
            catch { return (null, null); }
        }

        /// <summary>The definition's display name for app chrome (headers, report title blocks) —
        /// DisplayName with GH's unsaved-changes star trimmed, so "truss*" never renders.</summary>
        static string? DocNameOf(GH_Document doc)
        {
            try
            {
                var name = doc.DisplayName;
                return string.IsNullOrWhiteSpace(name) ? null : name.TrimEnd('*', ' ');
            }
            catch { return null; }
        }

        /// <summary>Resolve one declared view to live data. The id may be a component OR a param
        /// — including a component's own output param by its instance guid (nested lookup), so a
        /// manifest can address exactly the output it means. A component id with a
        /// <c>param</c> selector shows that named output; without one, the first non-stdout
        /// output (the pure <see cref="AppViewSelection"/> rule — "first output" pinned every
        /// script component's card to the empty <c>out</c> placeholder). Failures stay per-view
        /// and honest: a missing object, a wrong param name, or an unreadable target renders as
        /// a warning row instead of failing the whole state read.</summary>
        static InputData BuildView(GH_Document doc, AppViewRef view)
        {
            var (param, name, label, failure) = ResolveView(doc, view);
            if (param is null || failure is not null)
                return new InputData(name, "item",
                    new TreeInfo(0, 0, true), new List<TypeCount>(), new List<DataSample>(),
                    new List<string> { failure ?? MissingViewNote(view.Id) }, label, Id: view.Id);

            // Per-view sample override (manifest "samples": N, clamped 1..500): a real table or
            // chart needs its rows, not a sample of them — round 5's 93-row schedule had to be
            // chunk-packed through the 5-sample default (S5.11i). Opt-in per view because big
            // counts ride EVERY solve's frame.
            var cap = view.Samples is { } n ? Math.Max(1, Math.Min(500, n)) : 5;
            var total = view.Samples is { } m ? Math.Max(1, Math.Min(500, m)) : 50;
            return ShapeParamData(param, name, cap, total) with { Label = label, Id = view.Id };
        }

        /// <summary>The note a view carries when its component left the document — the id and the
        /// recovery, the same disclosure a missing CONTROL makes (round-9 S9.38: controls named
        /// their id, views said a bare "missing from the document").</summary>
        static string MissingViewNote(Guid id)
            => $"missing from the document — {id} left the canvas (ctrl-Z in Grasshopper restores it)";

        /// <summary><c>ParamName</c> is the bare addressable name (matches the manifest's own
        /// <c>param</c> and api/geometry's selector); <c>Label</c> is the qualified display
        /// string ("&lt;component nick&gt; &lt;param&gt;"). Serving the qualified string AS the
        /// param made every client-side manifest-name match silently miss (S5.11h).</summary>
        static (IGH_Param? Param, string ParamName, string Label, string? Failure) ResolveView(
            GH_Document doc, AppViewRef view)
        {
            var obj = doc.FindObject(view.Id, false);
            switch (obj)
            {
                case IGH_Param p:
                    var pName = p.NickName is { Length: > 0 } nick ? nick : (p.Name ?? "view");
                    return (p, pName, pName, null);
                case IGH_Component c:
                    var owner = c.NickName is { Length: > 0 } cn ? cn : (c.Name ?? "component");
                    var keys = c.Params.Output.Select(ParamKey).ToList();
                    var (index, error) = AppViewSelection.PickOutput(keys, view.Param);
                    if (error is not null)
                    {
                        var missName = string.IsNullOrEmpty(view.Param) ? owner : view.Param!;
                        var missLabel = string.IsNullOrEmpty(view.Param) ? owner : $"{owner} {view.Param}";
                        return (null, missName, missLabel, error);
                    }
                    var output = c.Params.Output[index];
                    return (output, ParamKey(output), $"{owner} {ParamKey(output)}", null);
                case null:
                    // Title the stub with the declared param when there is one: with the object
                    // gone there is no nickname to qualify with, and a bare 36-char guid where
                    // every other card has a name reads as a glitch (round-6 S6.8e).
                    var gone = view.Param ?? view.Id.ToString();
                    return (null, gone, gone, MissingViewNote(view.Id));
                default:
                    var oName = obj.NickName is { Length: > 0 } on ? on : view.Id.ToString();
                    return (null, oName, oName, "not a readable param");
            }
        }

        public AppGeometry ReadAppGeometry(AppViewRef view)
        {
            var doc = Doc(); // read path: fetching geometry from a background tab is fine
            var (param, _, label, failure) = ResolveView(doc, view);
            if (param is null || failure is not null)
                return new AppGeometry(label, Array.Empty<AppMesh>(), Array.Empty<AppPolyline>(),
                    Array.Empty<float>(), null, 0, 0, 0,
                    new[] { failure ?? MissingViewNote(view.Id) });

            var budget = new GeometryBudget();
            var meshes = new List<AppMesh>();
            var curves = new List<AppPolyline>();
            var points = new List<float>();
            var skippedTypes = new Dictionary<string, int>();
            var bounds = Rhino.Geometry.BoundingBox.Empty;
            var itemCount = 0;

            foreach (var path in param.VolatileData.Paths)
            {
                foreach (var item in param.VolatileData.get_Branch(path))
                {
                    itemCount++;
                    var value = (item as IGH_Goo)?.ScriptVariable() ?? item;
                    ConvertGeometryItem(value, budget, meshes, curves, points, ref bounds, skippedTypes);
                }
            }

            var warnings = new List<string>();
            if (budget.BudgetWarning(itemCount) is { } capped) warnings.Add(capped);
            foreach (var kv in skippedTypes.OrderBy(kv => kv.Key))
                warnings.Add($"skipped {kv.Value} item(s) of {kv.Key} — not viewport-convertible");

            return new AppGeometry(
                label, meshes, curves, points,
                bounds.IsValid
                    ? new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z, bounds.Max.X, bounds.Max.Y, bounds.Max.Z }
                    : null,
                itemCount, budget.TakenCount, budget.VertexCount, warnings);
        }

        /// <summary>One tree item into viewport form: meshes stay meshes, breps/surfaces/boxes
        /// mesh with the fast render preset, curves sample to polylines, points pass through.
        /// Anything else is counted and named — the page renders the truth, never a silently
        /// thinner model. Budget rules: items taken whole or skipped whole.</summary>
        static void ConvertGeometryItem(
            object? value, GeometryBudget budget,
            List<AppMesh> meshes, List<AppPolyline> curves, List<float> points,
            ref Rhino.Geometry.BoundingBox bounds, Dictionary<string, int> skippedTypes)
        {
            switch (value)
            {
                case Rhino.Geometry.Mesh mesh:
                    TakeMeshes(new[] { mesh }, budget, meshes, ref bounds);
                    return;
                case Rhino.Geometry.Brep brep:
                    TakeMeshes(Rhino.Geometry.Mesh.CreateFromBrep(
                        brep, Rhino.Geometry.MeshingParameters.FastRenderMesh), budget, meshes, ref bounds);
                    return;
                case Rhino.Geometry.Extrusion extrusion:
                    TakeMeshes(Rhino.Geometry.Mesh.CreateFromBrep(
                        extrusion.ToBrep(), Rhino.Geometry.MeshingParameters.FastRenderMesh), budget, meshes, ref bounds);
                    return;
                case Rhino.Geometry.Surface surface:
                    TakeMeshes(Rhino.Geometry.Mesh.CreateFromBrep(
                        surface.ToBrep(), Rhino.Geometry.MeshingParameters.FastRenderMesh), budget, meshes, ref bounds);
                    return;
                case Rhino.Geometry.Box box when box.IsValid:
                    TakeMeshes(Rhino.Geometry.Mesh.CreateFromBrep(
                        box.ToBrep(), Rhino.Geometry.MeshingParameters.FastRenderMesh), budget, meshes, ref bounds);
                    return;
                case Rhino.Geometry.Curve curve:
                    TakeCurve(curve, budget, curves, ref bounds);
                    return;
                case Rhino.Geometry.Line line:
                    TakePolyline(new[] { line.From, line.To }, budget, curves, ref bounds);
                    return;
                case Rhino.Geometry.Polyline polyline:
                    TakePolyline(polyline.ToArray(), budget, curves, ref bounds);
                    return;
                case Rhino.Geometry.Point3d point:
                    TakePoint(point, budget, points, ref bounds);
                    return;
                case Rhino.Geometry.Point rhinoPoint:
                    TakePoint(rhinoPoint.Location, budget, points, ref bounds);
                    return;
                case null:
                    Count(skippedTypes, "null");
                    return;
                default:
                    Count(skippedTypes, value.GetType().FullName ?? value.GetType().Name);
                    return;
            }

            static void Count(Dictionary<string, int> map, string key)
                => map[key] = map.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        static void TakeMeshes(
            IEnumerable<Rhino.Geometry.Mesh>? parts, GeometryBudget budget,
            List<AppMesh> meshes, ref Rhino.Geometry.BoundingBox bounds)
        {
            if (parts is null) return;
            var usable = parts.Where(m => m is { IsValid: true } && m.Vertices.Count > 0).ToList();
            if (usable.Count == 0) return;
            var vertexTotal = usable.Sum(m => m.Vertices.Count);
            if (!budget.TryTake(vertexTotal)) return;

            // One item = one AppMesh, however many faces the mesher split it into — merged
            // with plain index offsetting so the page never re-derives which parts belong
            // together.
            var positions = new List<float>(vertexTotal * 3);
            var indices = new List<int>();
            var offset = 0;
            foreach (var m in usable)
            {
                foreach (var v in m.Vertices)
                {
                    positions.Add(v.X); positions.Add(v.Y); positions.Add(v.Z);
                }
                foreach (var f in m.Faces)
                    GeometryBudget.AppendFace(indices, offset + f.A, offset + f.B, offset + f.C, offset + f.D, f.IsQuad);
                offset += m.Vertices.Count;
                var box = m.GetBoundingBox(false);
                if (box.IsValid) bounds.Union(box);
            }
            meshes.Add(new AppMesh(positions, indices));
        }

        static void TakeCurve(
            Rhino.Geometry.Curve curve, GeometryBudget budget,
            List<AppPolyline> curves, ref Rhino.Geometry.BoundingBox bounds)
        {
            if (curve.TryGetPolyline(out var polyline))
            {
                TakePolyline(polyline.ToArray(), budget, curves, ref bounds);
                return;
            }
            var t = curve.DivideByCount(GeometryBudget.CurvePointCap - 1, includeEnds: true);
            if (t is null || t.Length == 0) return;
            TakePolyline(t.Select(curve.PointAt).ToList(), budget, curves, ref bounds);
        }

        static void TakePolyline(
            IReadOnlyList<Rhino.Geometry.Point3d> pts, GeometryBudget budget,
            List<AppPolyline> curves, ref Rhino.Geometry.BoundingBox bounds)
        {
            if (pts.Count < 2 || !budget.TryTake(pts.Count)) return;
            var flat = new List<float>(pts.Count * 3);
            foreach (var p in pts)
            {
                flat.Add((float)p.X); flat.Add((float)p.Y); flat.Add((float)p.Z);
                bounds.Union(p);
            }
            curves.Add(new AppPolyline(flat));
        }

        static void TakePoint(
            Rhino.Geometry.Point3d p, GeometryBudget budget,
            List<float> points, ref Rhino.Geometry.BoundingBox bounds)
        {
            if (!budget.TryTake(1)) return;
            points.Add((float)p.X); points.Add((float)p.Y); points.Add((float)p.Z);
            bounds.Union(p);
        }

        // --- Helpers -----------------------------------------------------------------------

        static IGH_DocumentObject Find(GH_Document doc, Guid id) =>
            doc.FindObject(id, true) ?? throw new ArgumentException(
                ErrorProtocol.NotFound(id, ScanWireify(doc, includeStagedData: false)), nameof(id));

        static IGH_Component AsComponent(IGH_DocumentObject obj) =>
            obj as IGH_Component ?? throw new InvalidOperationException($"Object {obj.InstanceGuid} is not a component.");

        static ComponentIntrospection Introspect(IGH_Component comp)
        {
            // Read after a layout pass, never before: bounds and grips are otherwise whatever
            // the last repaint left (round-8 F1 — placeholders indistinguishable from readings).
            LayOut(comp);
            return new(
                comp.InstanceGuid,
                comp.Name ?? "",
                comp.NickName ?? "",
                comp.Params.Input.Select(p => ToParamInfo(p)).ToList(),
                comp.Params.Output.Select(p => ToParamInfo(p)).ToList(),
                BoundsOf(comp));
        }

        /// <summary>Lay an object out on demand. Grasshopper lays attributes out lazily, at the
        /// next canvas repaint: an object created, renamed, or re-parameterized while the window
        /// is not painting keeps stale or default bounds until then, and a tool reading them
        /// reports placeholders that look like readings (round-8 F1 — a control "piled at
        /// 1000,1000" sat on the canvas exactly where it belonged). Every mutation that changes
        /// a footprint calls this, and every read of bounds/grips does too. Best-effort: an
        /// attribute class that refuses to lay out leaves bounds null, which is honest.</summary>
        static void LayOut(IGH_DocumentObject obj)
        {
            try
            {
                if (obj.Attributes is not { } attrs) return;
                attrs.ExpireLayout();
                attrs.PerformLayout();
            }
            catch { /* null bounds say "not laid out"; a placeholder would lie */ }
        }

        /// <summary>A renamed object with something immediately to its RIGHT keeps its right edge
        /// where it was. Grasshopper grows a slider rightward from a fixed left edge, and a
        /// row-anchored control sits 30 px left of the component it was placed for — so a longer
        /// app-facing name walked straight over the input it was anchored to (round-9
        /// S9.29/S9.30: the anchored input fully occluded, and no tool could take it back). The
        /// anchor is GEOMETRIC (<see cref="CanvasPlacement.AnchoredToRight"/>) or a wired
        /// recipient to the right: round 11 (S11.22) renamed a control placed against its
        /// component but not yet wired to it, and a wiring-only test let it grow over the input.
        /// Objects with nothing to their right keep Grasshopper's own convention (left edge
        /// fixed). Returns false when the edge was meant to hold and measurably did not.</summary>
        static bool KeepAnchoredEdge(IGH_DocumentObject obj, System.Drawing.RectangleF rectBefore)
        {
            try
            {
                if (rectBefore.Width <= 0 || obj.Attributes is not { } attrs) return true;
                if (Math.Abs(attrs.Bounds.Width - rectBefore.Width) < 0.5f) return true;

                var before = new PlacementRect(rectBefore.X, rectBefore.Y, rectBefore.Width, rectBefore.Height);
                var anchored = false;
                if (obj.OnPingDocument() is { } doc)
                {
                    var neighbours = new List<PlacementRect>();
                    foreach (var other in doc.Objects)
                    {
                        if (ReferenceEquals(other, obj)) continue;
                        if (other.Attributes is { Bounds: { Width: > 0 } b })
                            neighbours.Add(new PlacementRect(b.X, b.Y, b.Width, b.Height));
                    }
                    anchored = CanvasPlacement.AnchoredToRight(before, neighbours);
                }
                if (!anchored && obj is IGH_Param param)
                {
                    foreach (var recipient in param.Recipients)
                    {
                        var top = recipient?.Attributes?.GetTopLevel;
                        if (top is not null && top.Bounds.Left >= rectBefore.Right - 1f) { anchored = true; break; }
                    }
                }
                if (!anchored) return true;
                return PinRightEdge(obj, attrs, rectBefore.Right);
            }
            catch { return true; /* placement is best-effort; the rename itself already landed */ }
        }

        /// <summary>Move the object so its right edge lands on <paramref name="right"/>: shift
        /// the pivot and lay out, then MEASURE; an attribute class that places from its bounds
        /// rather than its pivot gets both set together (the recipe the rename's undo action
        /// applies, verified bit-identical in round 11). False when the edge still did not land.</summary>
        static bool PinRightEdge(IGH_DocumentObject obj, IGH_Attributes attrs, float right)
        {
            var shift = right - attrs.Bounds.Right;
            if (Math.Abs(shift) < 0.5f) return true;
            attrs.Pivot = new System.Drawing.PointF(attrs.Pivot.X + shift, attrs.Pivot.Y);
            LayOut(obj);
            if (Math.Abs(attrs.Bounds.Right - right) < 0.5f) return true;

            var b = attrs.Bounds;
            var dx = right - b.Right;
            attrs.Pivot = new System.Drawing.PointF(attrs.Pivot.X + dx, attrs.Pivot.Y);
            attrs.Bounds = new System.Drawing.RectangleF(b.X + dx, b.Y, b.Width, b.Height);
            LayOut(obj);
            return Math.Abs(attrs.Bounds.Right - right) < 0.5f;
        }

        /// <summary>The object's laid-out canvas rectangle (the callers lay it out on demand),
        /// null only when its attributes refuse to lay out — placement is verifiable by an agent
        /// through this instead of a human screenshot (round-7 §7 J4).</summary>
        static CanvasRect? BoundsOf(IGH_DocumentObject obj)
        {
            try
            {
                if (obj.Attributes is { Bounds: { Width: > 0 } b })
                    return new CanvasRect(b.X, b.Y, b.Width, b.Height);
            }
            catch { /* attributes not laid out yet */ }
            return null;
        }

        static ParamInfo ToParamInfo(IGH_Param p, bool includeWiring = true, bool includeLayout = true)
        {
            // TypeName never reflects a script param's hint (it is the goo type, "Generic Data"
            // on every script variable), so the hint is reported explicitly — with the available
            // names, so a caller choosing one picks from reality instead of guessing.
            var availableHints = RhinoCodeInterop.GetAvailableHintNames(p);
            // Live wiring, both directions — the mechanical answer to "is this component spare?".
            // Ledger claims about wiring go stale between sessions; this read never does. The
            // document graph carries the wiring as edges instead and leaves the lists out;
            // the counts stay, so a mega-fanout is visible either way.
            var (sources, sourceCount) = WireEnds(p.Sources);
            var (recipients, recipientCount) = WireEnds(p.Recipients);
            return new ParamInfo(
                p.Name ?? "", p.NickName ?? "", AccessOf(p), p.TypeName ?? "", p.Optional,
                RhinoCodeInterop.GetSelectedHintName(p),
                availableHints.Count > 0 ? availableHints : null,
                includeWiring ? sources : null, includeWiring ? recipients : null, sourceCount, recipientCount,
                p.InstanceGuid,
                includeLayout ? GripYOf(p) : null);
        }

        /// <summary>The canvas row of the param's wire grip: the input grip on inputs, the output
        /// grip on outputs and floating params — the row <c>nearInput</c> placement aligns with.</summary>
        static float? GripYOf(IGH_Param p)
        {
            try
            {
                if (p.Attributes is not { } attrs) return null;
                var grip = p.Kind == GH_ParamKind.input ? attrs.InputGrip : attrs.OutputGrip;
                return grip.Y;
            }
            catch { return null; }
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
