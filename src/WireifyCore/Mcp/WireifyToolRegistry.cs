// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using ModelContextProtocol.Server;
using WireifyCore.Bridge;

namespace WireifyCore.Mcp
{
    /// <summary>
    /// Builds the MCP tool collection for a <see cref="WireifyTools"/> instance using the confirmed
    /// <c>McpServerTool.Create(Delegate, McpServerToolCreateOptions)</c> surface. Host-agnostic: the
    /// same collection feeds the <c>.Core</c> + <c>HttpListener</c> host (and the Kestrel fallback).
    /// Names + descriptions are deliberately rich + BM25-friendly (Tool-Search), and read tools set
    /// <c>UseStructuredContent</c> so their DTO payloads process in client-side code execution.
    /// </summary>
    public static class WireifyToolRegistry
    {
        public static McpServerPrimitiveCollection<McpServerTool> Build(WireifyTools t)
        {
            if (t is null) throw new ArgumentNullException(nameof(t));
            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            void Add(Delegate method, McpServerToolCreateOptions options) => tools.Add(McpServerTool.Create(method, options));

            Add((Func<bool, int, string?, DocumentSummary>)t.GetDocumentSummary, new McpServerToolCreateOptions
            {
                Name = "get_document_summary",
                Description = "List what is on THIS SESSION'S Grasshopper definition: each component's id, name, and nickname, plus "
                    + "the definition's .gh file path (activeFilePath) and isActiveCanvas, plus the Wireify registry - numbered "
                    + "staged sockets (with their staged input names) and converted W-numbered Python components. Use it to resolve "
                    + "'do #3' to a component id. Every wireify tool is bound to the definition this session was Connected for - "
                    + "with several files open, other definitions are never read or touched; isActiveCanvas: false means your "
                    + "definition is open but not the front tab (reads keep working; mutations refuse until the user brings it to "
                    + "front). unsavedChanges and savedAt (the saved file's UTC mtime) say whether the document differs from its "
                    + "file - an object count that disagrees with a saved baseline is not drift when the document is simply "
                    + "unsaved, so check them before raising an alarm. Pass includeStagedData: true to "
                    + "also get the live data on each staged socket's wired inputs (read_input_data shape, default caps) - a socket "
                    + "task then orients in this ONE call; separate read_input_data calls are only for deeper samples. On "
                    + "production-size canvases the components list caps at maxComponents (default 300, selected + W-numbered kept "
                    + "first; componentsTruncated and totalObjectCount report the cut; the wireify registry is never truncated) - "
                    + "narrow with nameFilter instead of re-listing. warnings (absent when none) is the registry's own alarm: "
                    + "two objects carrying one number — 'do #n' resolves by number, so never guess between them; a pasted "
                    + "socket renumbers itself on its next solve, otherwise rename one.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<IReadOnlyList<ComponentIntrospection>>)t.IntrospectSelected, new McpServerToolCreateOptions
            {
                Name = "introspect_selected",
                Description = "Introspect the currently selected object(s) - components (input and output parameters with names, "
                    + "data access item/list/tree, and types) and floating params (panels, sliders), which report as their own single output. "
                    + "Each param carries its LIVE wiring: sources (what feeds an input) and recipients (what consumes an output).",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, ComponentIntrospection>)t.IntrospectComponent, new McpServerToolCreateOptions
            {
                Name = "introspect_component",
                Description = "Introspect one object by id: a component's input and output parameters with names, data access "
                    + "(item/list/tree), and types; a floating param (panel, slider) reports as its own single output. Each param "
                    + "carries its LIVE wiring - sources (what feeds an input) and recipients (what consumes an output), with "
                    + "component ids and nicknames (capped at 50 per param; sourceCount/recipientCount are the true totals). This "
                    + "is the mechanical answer to 'what already feeds/consumes this?' - check it before wiring into or reusing "
                    + "any existing component; wiring claims remembered from earlier sessions go stale. bounds (canvas "
                    + "x/y/width/height) and each param's gripY (the canvas row of its wire grip) make placement checkable "
                    + "without a screenshot - the object is laid out on demand before reporting, so the numbers hold even "
                    + "while the Grasshopper window is not repainting: a control created with nearId + nearInput should sit "
                    + "LEFT of the component (its bounds' right edge below the component's x) with its vertical centre on "
                    + "that input's gripY.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid[]?, bool, bool, int, string?, DocumentGraph>)t.GetDocumentGraph, new McpServerToolCreateOptions
            {
                Name = "get_document_graph",
                Description = "Read THIS SESSION'S definition's wiring in ONE call: every object (components and floating "
                    + "params — panels, sliders, value lists — with id, name, nickname, kind, category and subCategory; name IS "
                    + "the type for a native, e.g. 'Polygon') plus every wire as an edge {from, fromParam, to, toParam} where "
                    + "from/to are INDICES into nodes (document order) and fromParam/toParam are the params' nicknames — an "
                    + "endpoint outside the returned nodes carries -1 and its guid in fromId/toId. A floating param is one "
                    + "param on both sides: wires INTO a panel or a relay param are edges too. Params ride along by default "
                    + "in a compact shape (name, nickName, access item/list/tree, typeName, optional, hint) WITHOUT per-param "
                    + "wiring lists, ids or layout — the edges are the wiring; availableHints (the script params' hint registry) "
                    + "is listed once on the graph. Pass includeOutputs: true to also inline each output's live data at small "
                    + "caps (3 samples per branch, 12 per output), so a port, an explanation, or a re-derivation of a definition "
                    + "orients in this one read instead of one introspect_component and one read_runtime_errors per component "
                    + "(36 + 32 serial calls on one real port before this existed). Pass ids to scope the read to a component "
                    + "and its neighbours (never trimmed; unknown ids come back in missingIds, absent when none). Whole-canvas "
                    + "reads are bounded two ways and truncation (absent when nothing was cut) names which bound: maxComponents "
                    + "(default 300) and a payload budget of roughly 15,000 tokens over the whole reply, selected + W-numbered "
                    + "nodes kept first — when nodesTruncated is true, read the rest with ids or nameFilter, or pass "
                    + "includeParams: false for the wiring alone. Persistent values of UNWIRED inputs are not exposed here or "
                    + "anywhere: read the component's OUTPUT (includeOutputs, or read_runtime_errors) — a Rotate's angle or a "
                    + "Mirror's plane shows in its transform output's text. read_runtime_errors keys outputs by NAME; edges and "
                    + "GraphParam.nickName carry the nickname — GraphParam has both. Layout numbers (bounds, gripY) and param "
                    + "ids stay with introspect_component, which lays the object out on demand; this read never touches layout.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, string, int, int, InputData>)t.ReadInputData, new McpServerToolCreateOptions
            {
                Name = "read_input_data",
                Description = "Read the live data on a wired input after the last solve: data-tree shape, a type histogram "
                    + "(goo wrapper vs unwrapped CLR type - un-hinted script variables receive the goo), and capped value "
                    + "samples each carrying its full valueLength. Use it to type generated Python to the data actually flowing in.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<RuntimeInfo>)t.GetRuntimeInfo, new McpServerToolCreateOptions
            {
                Name = "get_runtime_info",
                Description = "Report the Rhino version, which Grasshopper Python runtimes (CPython3, IronPython2) are available, "
                    + "and the loaded Wireify build (wireifyBuild, e.g. '0.2.0 build 2026-07-09 09:47'). If observed tool behavior "
                    + "ever contradicts the documented feature set, check wireifyBuild FIRST - a stale plugin install fails "
                    + "silently by simply lacking features, and this field is the mechanical way to catch it.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, ScriptSource>)t.GetSource, new McpServerToolCreateOptions
            {
                Name = "get_source",
                Description = "Read a script component's current source code - works on Rhino 8 script components and legacy "
                    + "GhPython (IronPython 2) alike. Step one of porting or reviewing existing scripts.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<PythonRuntime, string?, Guid>)t.CreatePythonComponent, new McpServerToolCreateOptions
            {
                Name = "create_python_component",
                Description = "Create a new Python script component on the canvas (CPython3 by default, or IronPython2) and return "
                    + "its id. Pass nickName — component nicknames are user-facing (the companion app renders them on every card "
                    + "and report heading; unnamed components all present as 'Py3'). Param names are user-facing too: name them "
                    + "for what they carry when you set_io (the stock x/y read as placeholder text on an app page).",
            });

            Add((Func<ControlSpec, AppControlState>)t.CreateControlComponent, new McpServerToolCreateOptions
            {
                Name = "create_control_component",
                Description = "Create a native Grasshopper control component on this session's canvas - the input kinds the "
                    + "companion web app can drive. kind: 'slider' (min/max, accuracy float|integer|even|odd, decimals for "
                    + "float, initial value - all snapped to the slider's own rounding), 'panel' (text), 'toggle' (flag), "
                    + "'valuelist' (items as name + value expression, e.g. 3 or \"steel\"; selected index), 'button', "
                    + "'mdslider' (a 2D point pad: xMin/xMax/yMin/yMax domains plus initial x/y), 'colour' (a colour swatch: "
                    + "colour as #RGB/#RRGGBB/#RRGGBBAA hex), or 'knob' (a dial: min/max, decimals, initial value). "
                    + "Give it a nickname, and pass nearId (e.g. the component it will feed) to place it left of that object - "
                    + "placement slides down past anything already there, so repeated creates stack into a clean column, never "
                    + "a pile. Creating one control per input? Pass nearInput (the input's name) with nearId to row-align each "
                    + "control with the input it feeds, the way engineers lay out a definition (and wire each control to ITS "
                    + "input). Returns the control's live state (the app-surface shape). One undo step - ctrl-Z removes it. "
                    + "Wire it into an input with the wire tool: a floating control is its own single output, index 0. Only "
                    + "these eight kinds - script components come from create_python_component; other native components stay "
                    + "manual gestures in Grasshopper.",
                UseStructuredContent = true,
            });

            Add((Func<Guid, string, PythonRuntime, bool, bool, SetSourceResult>)t.SetSource, new McpServerToolCreateOptions
            {
                Name = "set_source",
                Description = "Inject Python source into a component, recompile, solve, and return the runtime report (messages + "
                    + "outputs shaped like read_input_data: tree shape, type histogram, capped samples with valueLength, true total) "
                    + "in one step - a normal revise or fix needs NO separate run or read_runtime_errors call, and tree preservation "
                    + "is verifiable from the report itself. The CPython3 language directive is added automatically. Idempotent - "
                    + "call again to fix. Pass solve: false to skip the solve on a known-heavy canvas and use run (a background task) instead. "
                    + "DRIFT GUARD: if the component's code was hand-edited outside Wireify since the last write (the stamp's "
                    + "fingerprint mismatches), the call refuses with WIREIFY_EXTERNAL_EDIT and embeds the current code - merge "
                    + "their edits into your revision, then (with the user's OK) call again with overwriteExternalEdits: true; "
                    + "the guard refuses every write while hand-edited code sits on the component, so the flag is required even "
                    + "after a correct merge - never pass it without the user's OK. SIGNATURE GUARD (SDK mode): source whose "
                    + "RunScript params do not match the component's current inputs refuses up front - the engine would silently "
                    + "rewrite the signature and orphan the body. Declare params first (set_io), then set the source.",
                Idempotent = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, IoParamSpec[], IoParamSpec[], ComponentIntrospection>)t.SetIo, new McpServerToolCreateOptions
            {
                Name = "set_io",
                Description = "Define a script component's inputs and outputs EXPLICITLY (name + access item/list/tree + optional "
                    + "type hint) - the reliable way to shape plain script-mode components; nothing is parsed from source. Omitted "
                    + "hints select the dynamic default (values reach the script as native types). Replaces the existing variable "
                    + "params (the stdout 'out' output is ALWAYS present alongside your declared outputs, on every script "
                    + "component, by design - not a stale leftover); wires on same-named inputs are preserved. To RENAME an input "
                    + "while keeping its wire, set the spec's name to the CURRENT param name and renameTo to the final one. Names "
                    + "are user-facing (app cards, report headings): name params for what they carry - a list of Lines is 'lines', "
                    + "never a shipped 'in1'/'x'. Returns the resulting introspection - each param's selected hint is echoed in "
                    + "its 'hint' field (typeName never reflects hints).",
                UseStructuredContent = true,
            });

            Add((Func<Guid, TypedIoResult>)t.SetTypedIo, new McpServerToolCreateOptions
            {
                Name = "set_typed_io",
                Description = "SDK-mode only: sync a script component's params from its RunScript method signature. Plain "
                    + "script-mode components derive NOTHING from source (inputs stay at the default x, y) - use set_io for those. "
                    + "The receipt says whether the param set actually changed (changed:false = signature and params already "
                    + "agreed - note set_source refuses a mismatched signature up front, so the usual order is set_io, then source).",
                Idempotent = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, int, Guid, int, WireMode, WireResult>)t.Wire, new McpServerToolCreateOptions
            {
                Name = "wire",
                Description = "Wire an upstream output into a downstream input, by zero-based index (one undo step; it solves "
                    + "after wiring, so a follow-up read sees live data). Either end "
                    + "may be a floating param (panel, slider, file path) - it is its own single param, addressed with index 0. "
                    + "An input that ALREADY has sources refuses by default (WIREIFY_INPUT_WIRED, document untouched) - wiring "
                    + "onto an occupied input silently merges branches, so that is never implicit: pass mode 'replace' to swap "
                    + "the existing wire(s) out or 'add' to merge deliberately. The result echoes what connected where, the mode, "
                    + "and any replaced sources - verify from the receipt, no follow-up read needed.",
                UseStructuredContent = true,
            });

            Add((Func<Guid, string, PanelText>)t.SetPanelText, new McpServerToolCreateOptions
            {
                Name = "set_panel_text",
                Description = "Write text into an existing Panel component by id (one undo step - ctrl-Z restores the old "
                    + "content) - e.g. put a file path into a panel feeding a Read File component, completing the big-payload "
                    + "bypass tool-side. Refuses anything that is not a Panel. Confirm before overwriting a panel the user "
                    + "authored.",
                Destructive = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, string, IoParamSpec[], PythonRuntime, string?, IoParamSpec[]?, ConvertStagedResult>)t.ConvertStaged, new McpServerToolCreateOptions
            {
                Name = "convert_staged",
                Description = "Convert a staged Wireify socket into a stock Python script component, in place. Params are built "
                    + "EXPLICITLY: inputs = the WIRED staged input names (pass access per input, or omit for all-tree) - an "
                    + "unwired staged input (the socket's spare default) is dropped from the built component unless you declare it "
                    + "explicitly, and every drop is named in the result's warnings; outputs = the "
                    + "given specs; nothing is derived from source, so write plain script-mode code that reads the staged names as "
                    + "variables and assigns each output. Un-hinted inputs auto-select a type hint from the live wired data (one "
                    + "mappable type -> its token; a mixed-type tree gets no hint plus a warning in the result - relay it to the "
                    + "user before building on that input). Staged inputs still carrying generic names (in1, in2) should be RENAMED "
                    + "as part of the conversion: set the spec's name to the staged name and renameTo to a meaningful one derived "
                    + "from what is wired (a list of Lines -> 'lines') - the wire carries across, the script must use the final "
                    + "name, and names are user-facing (app cards, report headings), so never ship in1/in2 on a component you "
                    + "built; tell the user what you renamed. Wires move onto the built inputs, the W-number nickname is kept, "
                    + "the socket is removed, it solves - all one undo step, and the result carries that first solve's runtime "
                    + "report (messages + outputs), so no follow-up read is needed. The stdout 'out' output is ALWAYS present "
                    + "alongside your declared outputs (every script component has it, by design - not a stale leftover). On a "
                    + "spec mismatch it makes NO changes and says "
                    + "what to fix. To revise a converted component later, use set_source (and set_io if the I/O must change).",
                UseStructuredContent = true,
            });

            Add((Func<Guid, DeletedComponent>)t.DeleteComponent, new McpServerToolCreateOptions
            {
                Name = "delete_component",
                Description = "Delete a Wireify-managed object from the canvas by id - a Wireify socket, a script component, or "
                    + "a native app control (the eight kinds create_control_component builds: slider, panel, toggle, value list, "
                    + "button, MD slider, colour swatch, dial knob) - cleanup of something created here that is no longer wanted. "
                    + "Refuses anything else. One undo step: ctrl-Z in Grasshopper restores the object and its wires.",
                Destructive = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, ClearBadgeResult>)t.ClearBadge, new McpServerToolCreateOptions
            {
                Name = "clear_badge",
                Description = "Remove one object's blue 'wireify' badge record - the deliberate exit from touch-badging, which "
                    + "is otherwise permanent (creating a component or writing a panel marks the object as Wireify-authored in "
                    + "the document itself). Use it when the user wants an object un-marked, e.g. after an accidental write to "
                    + "their own panel. Honest mechanics, relay them when asked: NOT an undo record (mirroring the touch), it "
                    + "persists at the user's next save (no Wireify session can save), touching the object again re-badges it, "
                    + "and a W<n>-numbered capsule follows the component's NICKNAME, not this record - renaming drops that one. "
                    + "A cleared component also leaves the registry's 'authored' listing. Stale ids are accepted as cleanup.",
                Destructive = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, string, bool, RenameResult>)t.RenameComponent, new McpServerToolCreateOptions
            {
                Name = "rename_component",
                Description = "Rename any canvas object by id, as one undo step (ctrl-Z restores the old name). Nicknames are the "
                    + "page's copy: the companion app shows a control's nickname on its card and the report prints it as the row "
                    + "label, so an unnamed Number Slider reads as an anonymous 'slider' everywhere - name the controls you "
                    + "declare (the manifest has no label field; the canvas nickname is the ONE name). Records the touch (the "
                    + "object gets the wireify badge; clear_badge is the exit). Refuses Wireify sockets - they are addressed by "
                    + "number; convert first. Renaming a converted W<n> component off its prefix drops its number ('do #n' stops "
                    + "resolving to it and the registry lists it as authored) - the receipt's note says so; keep the 'W<n> ' "
                    + "prefix to keep the number. clear: true un-names the object deliberately (its card and row read by "
                    + "kind again) - the way back after a rename the user did not want; a plain empty nickName is refused.",
                UseStructuredContent = true,
            });

            Add((Func<Guid, RunResult>)t.Run, new McpServerToolCreateOptions
            {
                Name = "run",
                Description = "Solve a component (expire + one recompute) and return the post-solve runtime report (messages + "
                    + "outputs shaped like read_input_data: tree shape, type histogram, capped samples) - no follow-up read needed. "
                    + "Works on script components, native components, and floating params alike (a param reports itself as its own "
                    + "single output); runCount: -1 means the target exposes no run counter (only script components do), not a "
                    + "failure. It re-solves, so it counts as a mutation - the session's definition must be the active canvas; to "
                    + "read current values WITHOUT re-solving (background tab included), use read_runtime_errors. Long solves "
                    + "ride the session's request timeout; Claude Code moves calls past two minutes to the background on its own.",
                UseStructuredContent = true,
            });

            Add((Func<Guid, bool, RuntimeReport>)t.ReadRuntimeErrors, new McpServerToolCreateOptions
            {
                Name = "read_runtime_errors",
                Description = "Re-read a component's runtime errors, warnings, and remarks plus its current outputs (shaped: tree "
                    + "shape, type histogram, capped samples) WITHOUT re-solving - works from a background tab, and on native "
                    + "components and floating params too (a leaf param reports itself as its own single output). This is the way "
                    + "to read a component's current values when a re-solve is unwanted or the definition is not the front tab. "
                    + "set_source, run, and convert_staged already return this report - use this to re-check later, to read a "
                    + "component they did not touch, or with includeDocument for the rest of the canvas.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<WireifyCore.Hosting.AppSurfaceInfo>)t.GetAppInfo, new McpServerToolCreateOptions
            {
                Name = "get_app_info",
                Description = "Get THIS SESSION'S companion web app surface: the browser URL (with its access token) where the "
                    + "definition's app is served, whether an app/ folder and app/manifest.json exist in the agent home, and the "
                    + "manifest's declared control/view counts. manifestError carries the parse failure (with its line) when the "
                    + "manifest exists but is not valid JSON; manifestWarnings lists declared entries the parse could not use "
                    + "(e.g. an id that is not a component guid) - fix what they name instead of guessing. Use it to hand the "
                    + "user their app link, or to check the app surface before building or editing an app. The link rotates "
                    + "every Rhino run (per-home token) - after a Rhino restart, call this again for the fresh link; old links "
                    + "answer a friendly expired page. The app itself is served by the plugin and keeps working with every "
                    + "terminal closed.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<string, WireifyCore.Hosting.ScaffoldAppResult>)t.ScaffoldApp, new McpServerToolCreateOptions
            {
                Name = "scaffold_app",
                Description = "Set up THIS SESSION'S companion-app folder in one call: creates <home>/app/ if needed, stamps the "
                    + ".ify app kit into app/kit/ (kit files are Wireify-owned and refreshed - never edit inside kit/; they are "
                    + "also re-stamped at every Connect), and seeds a styled app/index.html plus an empty app/manifest.json "
                    + "ONLY when those files are absent - an existing page, manifest, or theme.css is never touched, so it is safe "
                    + "to re-run for kit updates. template picks the seeded page shape: panel (control-panel starter, default) or "
                    + "report (live-report: hero viewport, KPI strip, views, controls drawer) - for 'present my definition' asks. "
                    + "Read app/kit/KIT.md for the kit's classes, binding API, and design rules before "
                    + "building the page. The receipt reports what the call DID (seeded vs skipped files, the applied template) "
                    + "plus the get_app_info payload with the browser URL to hand the user - report the seeded/skipped truth, "
                    + "never guess it from the state flags.",
                UseStructuredContent = true,
            });

            return tools;
        }
    }
}
