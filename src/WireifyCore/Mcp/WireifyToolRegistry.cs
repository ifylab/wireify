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
                    + "front). Pass includeStagedData: true to "
                    + "also get the live data on each staged socket's wired inputs (read_input_data shape, default caps) - a socket "
                    + "task then orients in this ONE call; separate read_input_data calls are only for deeper samples. On "
                    + "production-size canvases the components list caps at maxComponents (default 300, selected + W-numbered kept "
                    + "first; componentsTruncated and totalObjectCount report the cut; the wireify registry is never truncated) - "
                    + "narrow with nameFilter instead of re-listing.",
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
                    + "any existing component; wiring claims remembered from earlier sessions go stale.",
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

            Add((Func<PythonRuntime, Guid>)t.CreatePythonComponent, new McpServerToolCreateOptions
            {
                Name = "create_python_component",
                Description = "Create a new Python script component on the canvas (CPython3 by default, or IronPython2) and return its id.",
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
                    + "after a correct merge - never pass it without the user's OK.",
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
                    + "component, by design - not a stale leftover); wires on same-named inputs are preserved. Returns the resulting "
                    + "introspection - each param's selected hint is echoed in its 'hint' field (typeName never reflects hints).",
                UseStructuredContent = true,
            });

            Add((Func<Guid, Guid>)t.SetTypedIo, new McpServerToolCreateOptions
            {
                Name = "set_typed_io",
                Description = "SDK-mode only: sync a script component's params from its RunScript method signature. Plain "
                    + "script-mode components derive NOTHING from source (inputs stay at the default x, y) - use set_io for those.",
                Idempotent = true,
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
                    + "user before building on that input). Wires move onto the same-named inputs, the W-number nickname is kept, "
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
                Description = "Delete a Wireify-managed object from the canvas by id - a Wireify socket or a script component "
                    + "(cleanup of something created here that is no longer wanted). Refuses anything else. One undo step: "
                    + "ctrl-Z in Grasshopper restores the object and its wires.",
                Destructive = true,
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
                    + "read current values WITHOUT re-solving (background tab included), use read_runtime_errors. The server "
                    + "surfaces long solves as a task.",
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

            return tools;
        }
    }
}
