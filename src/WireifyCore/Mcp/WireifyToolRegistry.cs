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

            Add((Func<bool, DocumentSummary>)t.GetDocumentSummary, new McpServerToolCreateOptions
            {
                Name = "get_document_summary",
                Description = "List what is on the Grasshopper canvas: each component's id, name, and nickname, plus the active .gh "
                    + "file path, plus the Wireify registry - numbered staged sockets (with their staged input names) and converted "
                    + "W-numbered Python components. Use it to resolve 'do #3' to a component id. Pass includeStagedData: true to "
                    + "also get the live data on each staged socket's wired inputs (read_input_data shape, default caps) - a socket "
                    + "task then orients in this ONE call; separate read_input_data calls are only for deeper samples.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<IReadOnlyList<ComponentIntrospection>>)t.IntrospectSelected, new McpServerToolCreateOptions
            {
                Name = "introspect_selected",
                Description = "Introspect the currently selected component(s): input and output parameters with names, data access (item/list/tree), and types.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, ComponentIntrospection>)t.IntrospectComponent, new McpServerToolCreateOptions
            {
                Name = "introspect_component",
                Description = "Introspect one component by id: its input and output parameters with names, data access (item/list/tree), and types.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, string, int, int, InputData>)t.ReadInputData, new McpServerToolCreateOptions
            {
                Name = "read_input_data",
                Description = "Read the live data on a wired input after the last solve: data-tree shape, a type histogram, and capped value samples. Use it to type generated Python to the data actually flowing in.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            Add((Func<RuntimeInfo>)t.GetRuntimeInfo, new McpServerToolCreateOptions
            {
                Name = "get_runtime_info",
                Description = "Report the Rhino version and which Grasshopper Python runtimes (CPython3, IronPython2) are available.",
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

            Add((Func<Guid, string, PythonRuntime, bool, SetSourceResult>)t.SetSource, new McpServerToolCreateOptions
            {
                Name = "set_source",
                Description = "Inject Python source into a component, recompile, solve, and return the runtime report (messages + "
                    + "fresh output values) in one step - a normal revise or fix needs NO separate run or read_runtime_errors call. "
                    + "The CPython3 language directive is added automatically. Idempotent - call again to fix. Pass solve: false to "
                    + "skip the solve on a known-heavy canvas and use run (a background task) instead.",
                Idempotent = true,
                UseStructuredContent = true,
            });

            Add((Func<Guid, IoParamSpec[], IoParamSpec[], ComponentIntrospection>)t.SetIo, new McpServerToolCreateOptions
            {
                Name = "set_io",
                Description = "Define a script component's inputs and outputs EXPLICITLY (name + access item/list/tree + optional "
                    + "type hint) - the reliable way to shape plain script-mode components; nothing is parsed from source. Replaces "
                    + "the existing variable params (the stdout 'out' stays); wires on same-named inputs are preserved. Returns the "
                    + "resulting introspection.",
                UseStructuredContent = true,
            });

            Add((Func<Guid, Guid>)t.SetTypedIo, new McpServerToolCreateOptions
            {
                Name = "set_typed_io",
                Description = "SDK-mode only: sync a script component's params from its RunScript method signature. Plain "
                    + "script-mode components derive NOTHING from source (inputs stay at the default x, y) - use set_io for those.",
                Idempotent = true,
            });

            Add((Action<Guid, int, Guid, int>)t.Wire, new McpServerToolCreateOptions
            {
                Name = "wire",
                Description = "Wire an upstream component output into a downstream component input, by zero-based index.",
            });

            Add((Func<Guid, string, IoParamSpec[], PythonRuntime, string?, IoParamSpec[]?, ConvertStagedResult>)t.ConvertStaged, new McpServerToolCreateOptions
            {
                Name = "convert_staged",
                Description = "Convert a staged Wireify socket into a stock Python script component, in place. Params are built "
                    + "EXPLICITLY: inputs = the staged input names (pass access per input, or omit for all-tree), outputs = the "
                    + "given specs; nothing is derived from source, so write plain script-mode code that reads the staged names as "
                    + "variables and assigns each output. Wires move onto the same-named inputs, the W-number nickname is kept, the "
                    + "socket is removed, it solves - all one undo step, and the result carries that first solve's runtime report "
                    + "(messages + outputs), so no follow-up read is needed. On a spec mismatch it makes NO changes and says what to "
                    + "fix. To revise a converted component later, use set_source (and set_io if the I/O must change).",
                UseStructuredContent = true,
            });

            Add((Func<Guid, RunResult>)t.Run, new McpServerToolCreateOptions
            {
                Name = "run",
                Description = "Solve a component (expire + recompute) and return the post-solve runtime report (messages + outputs) "
                    + "- no follow-up read needed. The server surfaces long solves as a task.",
                UseStructuredContent = true,
            });

            Add((Func<Guid, bool, RuntimeReport>)t.ReadRuntimeErrors, new McpServerToolCreateOptions
            {
                Name = "read_runtime_errors",
                Description = "Re-read a component's runtime errors, warnings, and remarks plus its current output values WITHOUT "
                    + "re-solving. Rarely needed: set_source, run, and convert_staged already return this report - use this only to "
                    + "re-check later or with includeDocument for the rest of the canvas.",
                ReadOnly = true,
                UseStructuredContent = true,
            });

            return tools;
        }
    }
}
