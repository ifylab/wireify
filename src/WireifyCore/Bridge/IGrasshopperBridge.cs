// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// The seam between the MCP tool surface and the live Grasshopper document. Every method maps
    /// to the validated spike recipe (create -> set source -> compile -> type -> wire -> run ->
    /// read). The MCP layer marshals these calls onto the GH UI thread; this interface itself is
    /// thread-agnostic and Rhino-type-free at its boundary.
    /// </summary>
    public interface IGrasshopperBridge
    {
        // --- Orientation (read-only) ---

        /// <summary>Document listing plus the Wireify registry. With
        /// <paramref name="includeStagedData"/> the staged sockets' wired inputs carry their live
        /// data (same shaping + caps as <see cref="ReadInputData"/>) — orientation in one call.
        /// The components list is bounded for production-size canvases (<paramref name="maxComponents"/>,
        /// &lt;=0 = no cap; optional <paramref name="nameFilter"/> substring); the registry never is.</summary>
        DocumentSummary GetDocumentSummary(
            bool includeStagedData = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents,
            string? nameFilter = null);

        ComponentIntrospection IntrospectComponent(Guid id);

        IReadOnlyList<ComponentIntrospection> IntrospectSelected();

        /// <summary>The definition's wiring in one read: every kept object (components and
        /// floating params, with their type identity and, unless declined, their params) and
        /// every wire touching them as an edge list. Bounded like the summary, or scoped to
        /// <paramref name="ids"/>. <paramref name="includeOutputs"/> inlines each output's live
        /// data at small caps — orientation for a port, an explanation, or a re-derivation in
        /// ONE call instead of one introspect per component (round-10 S10.6).</summary>
        DocumentGraph GetDocumentGraph(
            IReadOnlyList<Guid>? ids = null,
            bool includeParams = true,
            bool includeOutputs = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents,
            string? nameFilter = null);

        /// <summary>Live read of one wired input after the last solve (the edge).</summary>
        InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50);

        RuntimeInfo GetRuntimeInfo();

        /// <summary>Read a script component's current source (Rhino 8 script components and legacy
        /// GhPython alike) — the port flow's step one.</summary>
        ScriptSource GetSource(Guid id);

        // --- Build (mutation) ---

        /// <summary>Emit a fresh Python component of the given runtime and add it to the document.
        /// <paramref name="nickName"/>, when given, names the component on creation — the app
        /// surface renders component nicknames on every card and report heading, so a
        /// from-scratch component should never present as the stock "Py3" (round-5 S5.0c).</summary>
        Guid CreatePythonComponent(PythonRuntime runtime, string? nickName = null);

        /// <summary>Create a native control component (number slider, panel, boolean toggle,
        /// value list, button, MD slider, colour swatch, or dial knob) with kind-specific
        /// configuration — the input kinds the companion app can drive. One undo record (ctrl-Z
        /// removes it). A spec that does not line up refuses with a named reason and changes
        /// nothing. Returns the fresh control's state, the same shape the app surface
        /// reports.</summary>
        AppControlState CreateControlComponent(ControlSpec spec);

        /// <summary>
        /// Inject generated source and recompile. On CPython 3 the <c>#! python 3</c> directive is
        /// prepended if absent (RH-96540 drops the language spec without it); omitted for IronPython 2.
        /// With <paramref name="solve"/> (the default) the component is solved after compiling and
        /// the post-solve report returned — outputs are never stale after a revision. A W-component
        /// whose stamped body fingerprint no longer matches was hand-edited outside Wireify: the
        /// write refuses with <see cref="ErrorProtocol.ExternalEditCode"/> (current code embedded)
        /// unless <paramref name="overwriteExternalEdits"/> deliberately discards those edits.
        /// </summary>
        RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true, bool overwriteExternalEdits = false);

        /// <summary>Auto-build typed I/O params from the script's variables (validated path).
        /// Returns the honest receipt: whether the param set actually changed, with the input
        /// names before and after — the engine's sync is a no-op whenever signature and params
        /// already agree, and echoing bare success there cost a round-4 tester four dead
        /// ends.</summary>
        TypedIoResult SetParametersFromScript(Guid id);

        /// <summary>Connect <paramref name="fromOutput"/> of one component into <paramref name="toInput"/>
        /// of another, as one undo record. Either end may be a floating param (panel, slider, file
        /// path) — it is its own single param, addressed with index 0. An occupied target input is
        /// governed by <paramref name="mode"/>: Strict (default) refuses with
        /// <see cref="ErrorProtocol.InputWiredCode"/>, Replace swaps the existing wire(s) out,
        /// Add merges deliberately.</summary>
        WireResult Wire(Guid fromId, int fromOutput, Guid toId, int toInput, WireMode mode = WireMode.Strict);

        /// <summary>Write text into an existing Panel component (one undo record) — e.g. a file
        /// path feeding a Read File component. Refuses any non-Panel object.</summary>
        PanelText SetPanelText(Guid id, string text);

        /// <summary>
        /// Convert a staged Wireify socket into a stock Python component in place: create at the
        /// socket's pivot, build the input/output params EXPLICITLY (inputs = the staged names;
        /// outputs = the given specs; the sanctioned ScriptVariableParam path — no script parsing),
        /// set source, migrate wires by name, keep the W-number nickname, remove the socket — one
        /// undo step. Refuses (no changes) when the specs do not line up with the staged inputs.
        /// </summary>
        ConvertStagedResult ConvertStaged(
            Guid socketId,
            string code,
            IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime,
            string? nicknameSlug,
            IReadOnlyList<IoParamSpec>? inputs);

        /// <summary>
        /// Define a script component's I/O explicitly (same ScriptVariableParam mechanism):
        /// replaces its variable params with the given specs (the stdout "out" param is kept),
        /// preserving wires on inputs whose name survives. Returns the resulting introspection.
        /// </summary>
        ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs);

        /// <summary>Delete a Wireify-managed object — a socket, a script component, or a native
        /// app control (the eight kinds create_control_component builds, so agent create/delete
        /// iteration closes the loop) — as one undo record, wires included, so ctrl-Z restores
        /// everything. Refuses any other object.</summary>
        DeletedComponent DeleteComponent(Guid id);

        /// <summary>Remove one object's badge record (the blue "wireify" capsule) from the
        /// document's touched set — the deliberate exit from touch-badging, which is otherwise
        /// permanent. Mirrors the touch itself: no undo record, persisted by the user's next
        /// save, re-badged by any later touch. The <c>W&lt;n&gt;</c> numbered capsule follows
        /// the component's nickname, not this record — the receipt's note says so when it
        /// applies. Accepts stale guids (clearing a record whose object is gone is cleanup,
        /// not an error).</summary>
        ClearBadgeResult ClearBadge(Guid id);

        /// <summary>Rename any canvas object except a Wireify socket (addressed by its number —
        /// convert it first) as one undo record. Nicknames are user-facing: the companion app
        /// renders a control's nickname on its card and the report prints it as the row label,
        /// and an unnamed native slider read as an anonymous "slider" on every page until this
        /// existed (round-7 finding 5). Records the touch (the object gets the badge; clear_badge
        /// is the exit). Renaming a converted <c>W&lt;n&gt;</c> component off its prefix drops
        /// its number — the receipt says so. An EMPTY name is the deliberate un-name (the tool
        /// gates it behind <c>clear: true</c>): the card and row read by kind again.</summary>
        RenameResult RenameComponent(Guid id, string nickName);

        // --- Run + read ---

        RunResult Run(Guid id);

        RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false);

        // --- Companion app (webapp surface) ---

        /// <summary>Current state of the app-declared params: control values plus the watched
        /// params' shaped live data. Read path — works from a background tab.</summary>
        AppState ReadAppState(AppQuery query);

        /// <summary>One geometry-marked view's data serialized for the browser viewport: meshes
        /// (breps/surfaces meshed with the fast render preset), curves sampled to polylines,
        /// points — budgeted, items taken whole or skipped whole, everything skipped named in
        /// the warnings. Read path, fetched on demand so meshing cost never rides the solve
        /// (SSE frames stay light; the page decides its refresh cadence).</summary>
        AppGeometry ReadAppGeometry(AppViewRef view);

        /// <summary>Set a first-class app control's value (slider, panel, toggle, value list,
        /// button, MD slider, colour swatch, dial knob) and recompute — the companion app's push
        /// path. The push value's JSON shape is matched against the control's real kind;
        /// mismatches refuse honestly. Requires the definition to be the FRONT tab exactly like
        /// every mutation: Grasshopper solves only the front document, so a value landing on a
        /// background tab would sit unsolved with its downstream outputs empty (round-7 finding
        /// 1) — the resolver refuses with <see cref="ErrorProtocol.DocNotActiveCode"/> instead,
        /// and the page says so. Drag-shaped pushes (slider, MD slider, knob) coalesce their undo per gesture — an
        /// explicitly bracketed gesture (see <see cref="SetAppGesture"/>) is ONE undo record
        /// however slowly it moved; markerless pushes coalesce by quiet gap. Discrete controls
        /// record one undo per push.</summary>
        AppSetResult SetAppControlValue(Guid id, AppPushValue value);

        /// <summary>An explicit gesture boundary from the app client: <c>open: true</c> at
        /// pointer-down, <c>false</c> at pointer-up (the kit sends both; raw pages may not).
        /// Every drag-shaped push inside the bracket amends ONE undo record regardless of drag
        /// speed — time-gap coalescing alone cannot tell a slow drag from two separate edits.
        /// No document mutation and no solve; an unclosed gesture expires after an idle timeout
        /// so a dropped pointer-up never glues later edits together. Returns the control's fresh
        /// state (a cheap read) so a marker-only push answers the SAME envelope as a value push
        /// — two response shapes on one endpoint threw the obvious client code (round-5
        /// S5.1m).</summary>
        AppControlState SetAppGesture(Guid id, bool open);

        /// <summary>Subscribe to the bound document's SolutionEnd: after every recompute the
        /// callback receives a fresh <see cref="AppState"/> built directly on the UI thread. The
        /// query is re-read from <paramref name="queryProvider"/> per solve — a manifest edit is
        /// live on the next recompute, never frozen at stream-open; a null query skips that push.
        /// The provider and the callback both run ON the UI thread inside the solve: keep them
        /// cheap, and NEVER call back into the bridge — the serialized gate may be held by the
        /// very call that triggered the solve, and blocking the UI thread on it deadlocks until
        /// timeout — hand the snapshot off and return. Dispose the handle to unsubscribe.
        /// <paramref name="onClosed"/>, when supported by the implementation, fires once (also on
        /// the UI thread — same rules) when the subscribed document is removed from the document
        /// server, so a stream can end honestly instead of going silent.
        /// <paramref name="onSolveStart"/> fires at SolutionStart — the moment the status pill
        /// should read stale — with no snapshot (mid-solve reads are neither cheap nor safe).
        /// <paramref name="onActiveChanged"/> fires with a fresh snapshot when the subscribed
        /// document is tab-switched in or out (GH_DocumentContext Loaded/Unloaded) — without it
        /// no frame marks the front-tab change and the pill lies until the next solve. All
        /// callbacks arrive ON the UI thread; hand off, never re-enter the bridge.</summary>
        IDisposable SubscribeSolutionEnd(
            Func<AppQuery?> queryProvider, Action<AppState> onSolution, Action<string>? onClosed = null,
            Action? onSolveStart = null, Action<AppState>? onActiveChanged = null);
    }
}
