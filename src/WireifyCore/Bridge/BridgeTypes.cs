// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WireifyCore.Bridge
{
    /// <summary>Which Grasshopper Python runtime a component targets.</summary>
    public enum PythonRuntime
    {
        CPython3,
        IronPython2,
    }

    // Plain DTOs returned across the bridge boundary. They carry no Grasshopper types, so the MCP
    // layer can document them as tool output schemas and the test layer can assert on them without
    // a Rhino install.

    public sealed record ComponentRef(Guid Id, string Name, string NickName);

    /// <summary>A Wireify-managed component: a staged socket ("staged"), a converted, now-stock
    /// Python component ("converted") still carrying its <c>W&lt;n&gt;</c> nickname, or a script
    /// component in the document's badge record without a <c>W&lt;n&gt;</c> name ("authored" —
    /// created with a custom nickname, or renamed since). The document itself is the registry:
    /// numbers come from the nickname convention (<c>Number</c> is 0 for unnumbered entries),
    /// membership from the badge record — a rename must not make Wireify forget its own work
    /// (round-6 S6.8a).</summary>
    public sealed record WireifyComponentInfo(
        int Number,
        Guid Id,
        string NickName,
        string State,
        IReadOnlyList<string> InputNames,
        IReadOnlyList<InputData>? StagedData = null);

    /// <summary><c>TotalObjectCount</c> is the true document size; when the components list was
    /// capped for a big canvas, <c>ComponentsTruncated</c> is true (selected and Wireify-managed
    /// objects are kept preferentially; the <c>Wireify</c> registry itself is never truncated).
    /// <c>IsActiveCanvas</c> is false when this session's definition is open but not the front
    /// tab — reads keep working there; mutations wait for it to be brought to front.
    /// <c>UnsavedChanges</c> is Grasshopper's own dirty flag and <c>SavedAt</c> the saved file's
    /// UTC mtime (null for a never-saved document): an object count that disagrees with a saved
    /// baseline is not drift when the document is simply unsaved — three rounds raised that
    /// false alarm before the flag was exposed (round-7 §7 B7).</summary>
    public sealed record DocumentSummary(
        string? ActiveFilePath,
        IReadOnlyList<ComponentRef> Components,
        IReadOnlyList<WireifyComponentInfo>? Wireify = null,
        int TotalObjectCount = 0,
        bool ComponentsTruncated = false,
        bool IsActiveCanvas = true,
        bool UnsavedChanges = false,
        string? SavedAt = null,
        // What the registry has to say about itself (absent when nothing): today, two objects
        // carrying one number — the one ambiguity "do #n" resolution cannot survive (S12.13).
        IReadOnlyList<string>? Warnings = null);

    /// <summary>An object's canvas footprint (Grasshopper canvas coordinates, +Y down) — exposed
    /// so placement is verifiable by an agent instead of by a human screenshot (round-7 §7 J4).</summary>
    public sealed record CanvasRect(float X, float Y, float Width, float Height);

    /// <summary>One end of a live wire: the document object a param connects to (its owning
    /// component, or the floating param itself) plus the param's name.</summary>
    public sealed record WireEndInfo(Guid ComponentId, string NickName, string Param);

    /// <summary><c>Hint</c> is the selected type hint on a script variable param ("" when none or
    /// not a script param) — <c>TypeName</c> never reflects hints, so this is the honest echo after
    /// a hint change. <c>AvailableHints</c> lists what the param's hint registry offers (script
    /// params only; null elsewhere). <c>Sources</c> (what feeds an input) and <c>Recipients</c>
    /// (what consumes an output) are the param's LIVE wiring — null when none, capped at 50 each;
    /// <c>SourceCount</c>/<c>RecipientCount</c> always carry the true totals, so a mega-fanout is
    /// visible without flooding the payload. <c>Id</c> is the param's own instance guid — an
    /// exact address for an app-manifest view; regenerated when the component's I/O is rebuilt,
    /// so prefer the name form for anything durable. <c>GripY</c> is the canvas Y of the
    /// param's wire grip (the input grip on inputs, the output grip elsewhere) — the row a
    /// control placed with <c>nearInput</c> aligns with.</summary>
    public sealed record ParamInfo(
        string Name,
        string NickName,
        string Access,
        string TypeName,
        bool Optional,
        string Hint = "",
        IReadOnlyList<string>? AvailableHints = null,
        IReadOnlyList<WireEndInfo>? Sources = null,
        IReadOnlyList<WireEndInfo>? Recipients = null,
        int SourceCount = 0,
        int RecipientCount = 0,
        // Nullable deliberately: a non-nullable Guid default is stored as a metadata null,
        // which the SDK's schema exporter cannot serialize as a Guid (registry build throws).
        Guid? Id = null,
        float? GripY = null);

    /// <summary><c>Bounds</c> is the object's canvas rectangle, laid out on demand at read time
    /// (null only when its attributes refuse to lay out — never a placeholder) — with the
    /// params' <c>GripY</c>, the mechanical answer to "did that control land where an engineer
    /// would put it".</summary>
    public sealed record ComponentIntrospection(
        Guid Id,
        string Name,
        string NickName,
        IReadOnlyList<ParamInfo> Inputs,
        IReadOnlyList<ParamInfo> Outputs,
        CanvasRect? Bounds = null);

    /// <summary>One param as the document graph carries it: the name, access, type and hint a
    /// port needs — nothing else. The param's own id, its wire counts and its layout stay with
    /// introspect_component; the hint registry is hoisted once onto the graph. Round 11
    /// (S11.43) measured the full <see cref="ParamInfo"/> shape blowing a whole-canvas read
    /// past the tool-result limit on a 50-object definition, 21 KB of it the same 35-entry
    /// hint list repeated 58 times.</summary>
    public sealed record GraphParam(
        string Name,
        string NickName,
        string Access,
        string TypeName,
        bool Optional,
        string Hint = "");

    /// <summary>One object in the document graph. <c>Kind</c> is "component" or "param" (a
    /// floating panel, slider, or value list — one param on both sides). <c>Name</c> is the
    /// type's name for a native ("Polygon"), <c>Category</c>/<c>SubCategory</c> where it lives
    /// on the ribbon — enough to read a definition without a screenshot, and no per-node type
    /// guid (a second 36-character id per node was a quarter of the payload, round-12 S12.2).
    /// <c>Inputs</c>/<c>Outputs</c> are the params without per-param wiring — the graph's
    /// <c>edges</c> ARE the wiring — and without layout numbers (introspect_component lays the
    /// object out and reports bounds/gripY when placement is the question). <c>OutputData</c>
    /// rides only on ask: each output's live data, small caps.</summary>
    public sealed record GraphNode(
        Guid Id,
        string Name,
        string NickName,
        string Kind,
        string Category,
        string SubCategory,
        IReadOnlyList<GraphParam>? Inputs = null,
        IReadOnlyList<GraphParam>? Outputs = null,
        IReadOnlyList<InputData>? OutputData = null);

    /// <summary>One live wire, addressed by node INDEX into the graph's <c>nodes</c> (document
    /// order): <c>From</c>/<c>To</c> are indices, <c>FromParam</c>/<c>ToParam</c> the params'
    /// nicknames (the labels on the wire grips). An endpoint outside the kept set carries
    /// <c>-1</c> and its guid in <c>FromId</c>/<c>ToId</c> instead, so it is still addressable
    /// with introspect_component. A floating param is its own source and its own target.</summary>
    public sealed record GraphEdge(
        int From,
        string FromParam,
        int To,
        string ToParam,
        Guid? FromId = null,
        Guid? ToId = null);

    /// <summary>The whole wiring of the session's definition in one read: the kept objects and
    /// every wire touching them (round-10 S10.6 — 68 serial calls before a port could start).
    /// Bounded two ways, and <c>Truncation</c> names which one bound: the node cap (like the
    /// summary; selected and Wireify-managed objects are kept first) and a serialized-size
    /// budget (round-11 S11.43: a count cap said nothing was cut while the payload overran the
    /// tool-result limit). <c>TotalObjectCount</c> is the true document size. A read scoped by
    /// <c>ids</c> skips the caps; <c>MissingIds</c> lists any id no object carries.
    /// <c>AvailableHints</c> is the script params' hint registry, once.</summary>
    public sealed record DocumentGraph(
        string? ActiveFilePath,
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges,
        int TotalObjectCount = 0,
        bool NodesTruncated = false,
        bool IsActiveCanvas = true,
        IReadOnlyList<Guid>? MissingIds = null,
        string? Truncation = null,
        IReadOnlyList<string>? AvailableHints = null);

    public sealed record TreeInfo(int PathCount, int DataCount, bool IsFlat);

    /// <summary><c>Clr</c> is the unwrapped native type (what a type hint or <c>.Value</c> yields);
    /// <c>Goo</c> is the Grasshopper wrapper class the script actually receives on an un-hinted
    /// param (e.g. <c>GH_Line</c>). Empty when the item is not goo-wrapped.</summary>
    public sealed record TypeCount(string TypeName, string Clr, int Count, string Goo = "");

    /// <summary><c>ValueLength</c> is the full length of the value's string form; <c>Value</c>
    /// itself is capped (a huge wire value must not flood the response). A length of exactly
    /// 32767 is the classic upstream-clip signature (GH panels truncate pasted text).</summary>
    public sealed record DataSample(string Path, string Value, string TypeName, int ValueLength = 0);

    /// <summary>
    /// The edge: a live read of the data on one wired input after a solve — tree shape, a type
    /// histogram, and capped samples — so generated Python can be typed to what is actually flowing.
    /// <c>Warnings</c> carries data-health flags (today: a text value at exactly the panel clip
    /// length, i.e. truncated upstream).
    /// </summary>
    /// <summary>For app views, <c>Param</c> is the BARE addressable name (what the manifest
    /// declares and what api/geometry takes) and <c>Label</c> is the qualified display string
    /// ("&lt;component nick&gt; &lt;param&gt;") — a page matches on <c>Param</c> and titles with
    /// <c>Label</c>; qualifying <c>Param</c> itself made every manifest-name match silently miss
    /// (round-5 S5.11h). Tool receipts (read_input_data, reports) leave <c>Label</c> null.
    /// <c>SamplesTruncated</c> is true when <c>Samples</c> holds fewer items than
    /// <c>Tree.DataCount</c> — the delivered list is a sample, not the data (S5.11i).</summary>
    public sealed record InputData(
        string Param,
        string Access,
        TreeInfo Tree,
        IReadOnlyList<TypeCount> Types,
        IReadOnlyList<DataSample> Samples,
        IReadOnlyList<string>? Warnings = null,
        string? Label = null,
        bool? SamplesTruncated = null,
        // The declaring component's id on an APP VIEW (api/geometry needs id + param; a page
        // otherwise had to carry it from its own manifest — round-9 S9.0a). Null, and absent
        // from the JSON, on every other InputData (component inputs, staged data).
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Id = null);

    /// <summary><c>RhinoCodeLoaded</c>: whether the RhinoCode/script assemblies are already loaded
    /// in this Rhino. False means the first Python-component create triggers their lazy
    /// initialisation — a pause on healthy installs, a known crash spot on fragile ones — so the
    /// agent can warn the user before the first create/convert. <c>WireifyBuild</c> is the loaded
    /// plugin build ("0.2.0 build 2026-07-09 09:47") — the mechanical answer to "did the swap
    /// take?" when a session's observed tool behavior contradicts the expected feature set.</summary>
    public sealed record RuntimeInfo(
        string RhinoVersion,
        IReadOnlyList<string> AvailableRuntimes,
        string PythonVersion,
        bool RhinoCodeLoaded = false,
        string WireifyBuild = "");

    /// <summary>A script component's current source, as read from the component (the port flow's
    /// first step: read the legacy code before regenerating it).</summary>
    public sealed record ScriptSource(Guid Id, string NickName, string Source);

    // --- Companion app (webapp surface) DTOs ---------------------------------------------

    /// <summary>One declared view: <c>Id</c> is a component or a param (a floating param, or a
    /// component's own output param addressed by its instance guid); <c>Param</c> optionally
    /// names WHICH output of a component to show (nickname-or-name, case-insensitive). Without
    /// it, the first non-stdout output is shown — "first output" would pin every script
    /// component's card to the empty <c>out</c> placeholder. Note: param instance guids are
    /// regenerated when a component's I/O is rebuilt (set_io), so the name form is the durable
    /// one; the guid form is for precision (e.g. duplicate names).</summary>
    /// <summary><c>Geometry</c> marks a view whose data the app renders in the 3D viewport: the
    /// manifest opt-in that authorizes <c>api/geometry</c> for it (meshing is real work — only
    /// views that asked for it serve meshes).</summary>
    /// <summary><c>Samples</c> optionally raises this view's sample delivery (per branch and in
    /// total) from the 5/50 default — a real table or chart needs the rows, not a sample of
    /// them. Clamped to 1..500 server-side; big values cost frame weight on every solve, so the
    /// page author opts in per view.</summary>
    public sealed record AppViewRef(Guid Id, string? Param = null, bool Geometry = false, int? Samples = null);

    /// <summary>The app-declared params one state snapshot covers: <c>Controls</c> are inputs the
    /// browser app reads AND pushes (number sliders first), <c>Views</c> are watched params whose
    /// live data the app renders. Derived from the home's <c>app/manifest.json</c> — never from a
    /// request body — so the server only ever touches declared params. <c>Warnings</c> carries
    /// manifest entries the parse could not use (an <c>id</c> that is not a component guid, a
    /// missing <c>id</c>) — the state frame relays them, because a silently dropped declaration
    /// reads as a plugin bug to the page author who just wrote it (round-6 S6.11j).</summary>
    public sealed record AppQuery(
        IReadOnlyList<Guid> Controls,
        IReadOnlyList<AppViewRef> Views,
        IReadOnlyList<string>? Warnings = null,
        IReadOnlyDictionary<Guid, AppControlText>? ControlText = null);

    /// <summary>A control's page-facing text from the manifest: <c>Label</c> is the human name
    /// the page shows in place of the canvas nickname (which stays visible beside it), <c>Help</c>
    /// a one-line hint under it. Neither reaches the canvas.</summary>
    public sealed record AppControlText(string? Label, string? Help);

    /// <summary>One axis of an MD slider's live state: current value plus its interval bounds
    /// (normalized min &lt;= max whatever direction the canvas interval was authored in).</summary>
    public sealed record AppAxisState(double Value, double Min, double Max);

    /// <summary>One app control's live state. <c>Kind</c> is "slider", "panel", "toggle",
    /// "valuelist", "button", "mdslider", "colour", or "knob" for the first-class controls;
    /// bounds/items/text ride along so the browser control derives everything from the canvas
    /// instead of restating it — a canvas edit re-shapes the app on the next frame. A declared
    /// param of any other kind reports "unsupported:&lt;name&gt;" ("missing" when gone from the
    /// document) and its pushes are refused. Only the fields for the control's kind are
    /// meaningful: sliders and knobs use Value/Min/Max/Decimals/Step, panels use Text, toggles
    /// use Bool, value lists use Items/Selected (plus Text = the selected item's name), buttons
    /// use Bool (true while pressed), MD sliders use Axes (2 or 3 entries per the slider's
    /// mode), colour swatches use Colour (#rrggbb, alpha byte appended only when not opaque).
    /// <c>Step</c> is the control's real increment — 1 for integer rounding, 2 for even/odd,
    /// 10^-decimals for float sliders and knobs — so a browser range control drags on values
    /// the canvas control can actually hold. A slider carrying a canvas expression reports
    /// <c>Evaluated</c> = the expression's result beside the raw <c>Value</c>: the app writes
    /// raw and reads raw (pushes stay idempotent), the evaluated number is display truth.</summary>
    // The numeric fields are nullable and stay null for kinds they do not describe (panel,
    // toggle, mdslider, colour, missing, …): a zero there reads as data to a generic widget —
    // an mdslider's payload carried a confident value 0 while the truth sat in Axes (round-5
    // S5.2b). Null is the honest "this kind has no scalar value".
    // Label and Help ride from app/manifest.json (the app surface fills them after the bridge
    // reads): a human name for the page and a one-line hint. The canvas nickname stays the
    // control's identity and the page keeps it visible beside the label.
    public sealed record AppControlState(
        Guid Id,
        string NickName,
        string Kind,
        double? Value = null,
        double? Min = null,
        double? Max = null,
        int? Decimals = null,
        string? Text = null,
        bool? Bool = null,
        IReadOnlyList<string>? Items = null,
        int? Selected = null,
        double? Step = null,
        IReadOnlyList<AppAxisState>? Axes = null,
        string? Colour = null,
        double? Evaluated = null,
        string? Label = null,
        string? Help = null);

    /// <summary>One triangle mesh for the browser viewport: xyz position triples plus triangle
    /// indices (quads pre-split tool-side — the page never re-derives topology). Positions are
    /// floats deliberately: display geometry, half the JSON of doubles.</summary>
    public sealed record AppMesh(IReadOnlyList<float> Positions, IReadOnlyList<int> Indices);

    /// <summary>One curve sampled for display: xyz triples along the curve.</summary>
    public sealed record AppPolyline(IReadOnlyList<float> Points);

    /// <summary>One view's geometry serialized for the browser viewport, budgeted and honest:
    /// <c>ItemCount</c> items were in the tree, <c>RenderedCount</c> made it under the vertex
    /// budget (items are skipped whole, never half a mesh), and <c>Warnings</c> names anything
    /// capped or unconvertible. <c>Bounds</c> = [minX,minY,minZ,maxX,maxY,maxZ] of the rendered
    /// geometry for camera fitting. Fetched on demand (<c>api/geometry</c>) rather than riding
    /// SSE frames — a drag's frames stay light, and meshing cost is paid per fetch, off the
    /// solve path.</summary>
    public sealed record AppGeometry(
        string Label,
        IReadOnlyList<AppMesh> Meshes,
        IReadOnlyList<AppPolyline> Curves,
        IReadOnlyList<float> Points,
        IReadOnlyList<double>? Bounds,
        int ItemCount,
        int RenderedCount,
        int VertexCount,
        IReadOnlyList<string> Warnings);

    /// <summary>One state snapshot for the companion app: the declared controls' current values
    /// plus the watched params' live data, shaped exactly like input reads (tree stats, type
    /// histogram, capped samples). <c>IsActiveCanvas</c> mirrors the summary flag and DRIVES the
    /// page's pill: Grasshopper solves only the front document, so value pushes need the
    /// definition in front exactly like every tool mutation (a background push refuses rather
    /// than parking an unsolved value — round-7 finding 1). <c>Warnings</c> relays manifest
    /// declarations the parse dropped (see <see cref="AppQuery"/>) so the page can show them.
    /// <c>DocUnits</c> (the Rhino document's unit system name) and <c>Tolerance</c> (its
    /// absolute tolerance) let a page label axes and a report label its numbers without a
    /// script plumbing units through as a view (round-7 §7 E6).</summary>
    public sealed record AppState(
        bool IsActiveCanvas,
        IReadOnlyList<AppControlState> Controls,
        IReadOnlyList<InputData> Views,
        string? DocName = null,
        IReadOnlyList<string>? Warnings = null,
        string? DocUnits = null,
        double? Tolerance = null);

    /// <summary>A value arriving from the app, shaped by its JSON type: a number, a string, or a
    /// boolean — exactly one is set. The bridge matches it against the target control's real kind
    /// (a slider or knob wants Number; a panel wants Text; a toggle or button wants Flag; a value
    /// list takes Number as an item index or Text as an item name; an MD slider takes Text as
    /// comma-separated axis values; a colour swatch takes Text as web hex) and refuses
    /// mismatches honestly.</summary>
    public sealed record AppPushValue(double? Number = null, string? Text = null, bool? Flag = null);

    /// <summary>A value push's receipt: the control's fresh state after the set (the page updates
    /// from this without waiting for the next stream frame) and whether the pushed value was
    /// clamped to the control's own bounds.</summary>
    public sealed record AppSetResult(AppControlState Control, bool Clamped);

    /// <summary>Receipt for a delete: which Wireify-managed object was removed. The removal is one
    /// undo record, so ctrl-Z in Grasshopper restores the object and its wires.</summary>
    public sealed record DeletedComponent(Guid Id, string Name, string NickName);

    /// <summary>Receipt for a panel write: which Panel now holds text of what length. One undo
    /// record — ctrl-Z restores the previous content.</summary>
    public sealed record PanelText(Guid Id, string NickName, int Length);

    /// <summary>How <c>wire</c> treats a target input that already has sources: <c>Strict</c>
    /// (default) refuses — an occupied input is never touched without an explicit choice;
    /// <c>Replace</c> swaps the existing wire(s) out; <c>Add</c> merges deliberately (branches
    /// combine).</summary>
    public enum WireMode
    {
        Strict,
        Add,
        Replace,
    }

    /// <summary>Receipt for a wire: what connected where (param names resolved from the indexes),
    /// in which mode, as one undo record. <c>ReplacedSources</c> lists the wire ends Replace
    /// removed (empty otherwise) — the agent verifies what happened without a follow-up read.</summary>
    public sealed record WireResult(
        Guid FromId,
        string FromOutput,
        Guid ToId,
        string ToInput,
        string Mode,
        IReadOnlyList<WireEndInfo> ReplacedSources);

    public sealed record RuntimeMessage(string Level, string Text);

    /// <summary>One output in a runtime report, shaped exactly like a read input: tree stats, a
    /// type histogram, and capped samples (each carrying its full <c>ValueLength</c>), plus the
    /// true total <c>Count</c> — so tree preservation is verifiable from the report itself and a
    /// heavy output can never flood the response or pin the UI thread.</summary>
    public sealed record OutputValue(
        string Param,
        TreeInfo Tree,
        IReadOnlyList<TypeCount> Types,
        IReadOnlyList<DataSample> Samples,
        int Count,
        IReadOnlyList<string>? Warnings = null);

    public sealed record RuntimeReport(
        IReadOnlyList<RuntimeMessage> Messages,
        IReadOnlyList<OutputValue> Outputs);

    /// <summary>Solve outcome plus the post-solve report, so the caller never needs a separate
    /// read to learn what the solve it just triggered said.</summary>
    public sealed record RunResult(bool Ran, int RunCount, RuntimeReport? Report = null);

    /// <summary>Result of set_source: the component solved by default after compiling, and
    /// <c>Report</c> carries that solve's messages + outputs (null when <c>solve</c> was false).</summary>
    public sealed record SetSourceResult(Guid Id, bool Solved, RuntimeReport? Report);

    /// <summary>Receipt for a params-from-script sync: whether the param set actually changed,
    /// with the input names before and after. The engine's sync is a no-op whenever the
    /// signature already matches the params — a bare success echo there reads as work done.</summary>
    public sealed record TypedIoResult(
        Guid Id, bool Changed, IReadOnlyList<string> InputsBefore, IReadOnlyList<string> InputsAfter);

    /// <summary>
    /// One scripted input or output parameter, declared explicitly (the McNeel-sanctioned
    /// <c>ScriptVariableParam</c> path — plain script-mode components never derive inputs from
    /// source). <c>Name</c> is the variable the script sees; <c>Access</c> is item/list/tree;
    /// <c>TypeHint</c> is an optional hint token from the component's own registry.
    /// </summary>
    public sealed record IoParamSpec(
        [property: Description("Variable name the script sees (for staged sockets: the staged input name; with renameTo: the CURRENT name being renamed).")] string Name,
        [property: Description("Data access: item, list, or tree.")] string Access = "item",
        [property: Description("Optional type-hint token — .NET-style names: string, double, int, bool, Line, Curve, Point3d, Brep ... (NOT Python names; 'str'/'float' do not exist). A wrong token fails loudly listing the real ones. On convert_staged inputs, omitted hints auto-select from the live wired data.")] string? TypeHint = null,
        [property: Description("Inputs only: the FINAL variable name, when it should differ from Name. Name stays the match key (the staged input on convert_staged, the current param on set_io) and the wire carries across — so a socket input left as 'in1' converts as 'lines' with its wire intact. The script must use this name.")] string? RenameTo = null);

    /// <summary>One item of a value list being created: the label shown in the list plus its
    /// expression (the value it emits — a number literal like <c>3</c>, or a quoted string like
    /// <c>"steel"</c>, exactly as Grasshopper's own value-list editor takes them).</summary>
    public sealed record ValueListEntry(
        [property: Description("Item label shown in the list.")] string Name,
        [property: Description("Item value expression — e.g. 3, 2.5, or \"steel\" (strings quoted, as in GH's own value-list editor).")] string Expression);

    /// <summary>
    /// What to create for <c>create_control_component</c>: one of the eight app-control kinds
    /// plus its kind-specific configuration. Only the fields for the chosen kind apply; the rest
    /// are ignored. A spec that does not line up (unknown kind, valuelist without items, min
    /// above max, unparseable colour) refuses with a named reason and changes nothing.
    /// </summary>
    public sealed record ControlSpec(
        [property: Description("Control kind: slider, panel, toggle, valuelist, button, mdslider, colour, or knob.")] string Kind,
        [property: Description("Nickname for the new control (e.g. 'height'). Optional.")] string? NickName = null,
        [property: Description("Slider/knob: domain minimum (default 0).")] double? Min = null,
        [property: Description("Slider/knob: domain maximum (default 10).")] double? Max = null,
        [property: Description("Slider/knob: decimal places for float rounding (default 3; ignored for integer/even/odd sliders).")] int? Decimals = null,
        [property: Description("Slider rounding: float (default), integer, even, or odd.")] string? Accuracy = null,
        [property: Description("Slider/knob: initial value (default the domain minimum; snapped to the control's own rounding).")] double? Value = null,
        [property: Description("Panel: initial text (default empty).")] string? Text = null,
        [property: Description("Toggle: initial state (default false).")] bool? Flag = null,
        [property: Description("Value list: the items, in order. Required for valuelist.")] IReadOnlyList<ValueListEntry>? Items = null,
        [property: Description("Value list: initially selected item index (default 0).")] int? Selected = null,
        [property: Description("Place the new control just left of this existing object (e.g. the component it will feed), sliding down past anything already there - repeated creates stack into a clean column, never a pile. Default: a free spot on the canvas.")] Guid? NearId = null,
        [property: Description("With NearId on a component: the INPUT NAME to row-align with - the control lands left of the component at that input's own height (the Extract Parameter convention), which is how engineers lay out a component's inputs. Use it when creating one control per input.")] string? NearInput = null,
        [property: Description("MD slider: X domain minimum (default 0).")] double? XMin = null,
        [property: Description("MD slider: X domain maximum (default 1).")] double? XMax = null,
        [property: Description("MD slider: Y domain minimum (default 0).")] double? YMin = null,
        [property: Description("MD slider: Y domain maximum (default 1).")] double? YMax = null,
        [property: Description("MD slider: initial X (default the X minimum; clamped into the domain).")] double? X = null,
        [property: Description("MD slider: initial Y (default the Y minimum; clamped into the domain).")] double? Y = null,
        [property: Description("Colour swatch: initial colour as web hex — #RGB, #RRGGBB, or #RRGGBBAA (default #ffffff).")] string? Colour = null);

    /// <summary>
    /// Result of converting a staged socket into a stock Python component. On a refusal (bad
    /// argument shape vs the staged inputs) the conversion makes NO changes: <c>Converted</c> is
    /// false, <c>Error</c> says why, and <c>ScriptInputs</c> reports the staged input names, so
    /// the caller can fix the arguments and call again. <c>Warnings</c> carries data-shape flags
    /// worth relaying to the user before building on the result — today: a mixed-type input tree
    /// no hint can fit (its geometry reaches the script as script-doc Guid references).
    /// </summary>
    // Error's default is load-bearing: a no-default property is REQUIRED in the generated
    // output schema while the SDK's serializer omits nulls — so every successful convert
    // (Error = null) failed client-side schema validation on validating clients while the
    // mutation had landed (round-5 S5.6g, the set_source solve:false class). The default makes
    // the schema mark it optional.
    public sealed record ConvertStagedResult(
        bool Converted,
        Guid NewComponentId,
        string NickName,
        IReadOnlyList<string> WiredInputs,
        IReadOnlyList<string> ScriptInputs,
        IReadOnlyList<string> Outputs,
        string? Error = null,
        RuntimeReport? Report = null,
        IReadOnlyList<string>? Warnings = null);

    /// <summary>Receipt for clear_badge: whether a badge record was removed for the id.
    /// <c>Cleared</c> false means there was nothing to clear (no write happened). <c>Note</c>
    /// carries the honest residue when there is one — a <c>W&lt;n&gt;</c> nickname still draws
    /// the numbered capsule (that one follows the nickname; renaming drops it). The change
    /// lives in the open document and persists at the user's next save — no Wireify session
    /// can save. Touching the object again re-badges it.</summary>
    public sealed record ClearBadgeResult(
        bool Cleared,
        string NickName = "",
        string? Note = null);

    /// <summary>Receipt for rename_component: the nickname before and after (one undo record).
    /// <c>Note</c> carries the honest residue when there is one — renaming a converted
    /// <c>W&lt;n&gt;</c> component off its prefix drops its number (<c>do #n</c> stops resolving
    /// to it; the registry lists it as authored), and the numbered capsule goes with it.</summary>
    public sealed record RenameResult(
        Guid Id,
        string NickNameBefore,
        string NickNameAfter,
        string? Note = null);
}
