// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using WireifyCore.Bridge;

namespace WireifyCore.Mcp
{
    /// <summary>
    /// The MCP tool surface: thin, input-validated delegation to <see cref="IGrasshopperBridge"/>,
    /// one method per tool. Parameter <see cref="DescriptionAttribute"/>s feed the generated input
    /// schema; the DTO return types document the output schema. Tool names + descriptions live in
    /// <see cref="WireifyToolRegistry"/>. Designed Tool-Search / code-execution friendly per the build plan.
    /// </summary>
    public sealed class WireifyTools
    {
        readonly IGrasshopperBridge _bridge;
        readonly Action<Guid, bool>? _activity;

        public WireifyTools(IGrasshopperBridge bridge, Action<Guid, bool>? activity = null)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _activity = activity;
        }

        /// <summary>Signals begin/end of a mutating tool call on a component, so the socket's
        /// attributes can show a live "Working" state while Claude edits it.</summary>
        T WithActivity<T>(Guid id, Func<T> work)
        {
            _activity?.Invoke(id, true);
            try { return work(); }
            finally { _activity?.Invoke(id, false); }
        }

        // --- Orientation (read-only) ---

        public DocumentSummary GetDocumentSummary(
            [Description("Also inline the live data on each staged socket's wired inputs (same shape + caps as read_input_data) — orientation for a 'do #n' task in one call (default false).")] bool includeStagedData = false)
            => _bridge.GetDocumentSummary(includeStagedData);

        public IReadOnlyList<ComponentIntrospection> IntrospectSelected() => _bridge.IntrospectSelected();

        public ComponentIntrospection IntrospectComponent(
            [Description("InstanceGuid of the component to introspect.")] Guid id)
            => _bridge.IntrospectComponent(id);

        public InputData ReadInputData(
            [Description("InstanceGuid of the component that owns the input.")] Guid id,
            [Description("Name or nickname of the input parameter to read.")] string inputParam,
            [Description("Max samples per data-tree branch (default 5).")] int maxPerBranch = 5,
            [Description("Max samples total across all branches (default 50).")] int maxTotal = 50)
            => _bridge.ReadInputData(id, Require(inputParam, nameof(inputParam)), maxPerBranch, maxTotal);

        public RuntimeInfo GetRuntimeInfo() => _bridge.GetRuntimeInfo();

        public ScriptSource GetSource(
            [Description("InstanceGuid of the script component whose source to read.")] Guid id)
            => _bridge.GetSource(id);

        // --- Build (mutation) ---

        public Guid CreatePythonComponent(
            [Description("Target runtime: CPython3 (default) or IronPython2.")] PythonRuntime runtime = PythonRuntime.CPython3)
            => _bridge.CreatePythonComponent(runtime);

        public SetSourceResult SetSource(
            [Description("InstanceGuid of the target Python component.")] Guid id,
            [Description("Python source to inject and compile.")] string source,
            [Description("Runtime the source targets (default CPython3).")] PythonRuntime runtime = PythonRuntime.CPython3,
            [Description("Solve after compiling and return the runtime report (default true). Pass false on heavy canvases and use run instead.")] bool solve = true)
        {
            var validated = Require(source, nameof(source));
            return WithActivity(id, () =>
            {
                var report = _bridge.SetSource(id, validated, runtime, solve);
                return new SetSourceResult(id, solve, report);
            });
        }

        public Guid SetTypedIo(
            [Description("InstanceGuid of the component whose params to (re)build from its script.")] Guid id)
            => WithActivity(id, () =>
            {
                _bridge.SetParametersFromScript(id);
                return id;
            });

        public void Wire(
            [Description("InstanceGuid of the upstream (source) component.")] Guid fromId,
            [Description("Zero-based output index on the upstream component.")] int fromOutput,
            [Description("InstanceGuid of the downstream (target) component.")] Guid toId,
            [Description("Zero-based input index on the downstream component.")] int toInput)
            => _bridge.Wire(fromId, fromOutput, toId, toInput);

        public ConvertStagedResult ConvertStaged(
            [Description("InstanceGuid of the staged Wireify socket to convert.")] Guid id,
            [Description("Plain script-mode Python: read the staged input names as variables, assign each declared output.")] string code,
            [Description("The output params to build, in order: name + access (+ optional type hint). Required — outputs are never derived from source.")] IoParamSpec[] outputs,
            [Description("Target runtime (default CPython3).")] PythonRuntime runtime = PythonRuntime.CPython3,
            [Description("Short kebab-case task slug for the nickname, e.g. 'cull-panels' -> 'W3 cull-panels'.")] string? nicknameSlug = null,
            [Description("Access (+ optional hint) per staged input, matched by name; must cover every staged input. Omit for all-tree, no hints.")] IoParamSpec[]? inputs = null)
        {
            var validated = Require(code, nameof(code));
            if (outputs is null || outputs.Length == 0)
                throw new ArgumentException("outputs is required.", nameof(outputs));
            return WithActivity(id, () => _bridge.ConvertStaged(id, validated, outputs, runtime, nicknameSlug, inputs));
        }

        public ComponentIntrospection SetIo(
            [Description("InstanceGuid of the script component whose I/O to define.")] Guid id,
            [Description("Input params to build, in order: name + access (item/list/tree) + optional type hint.")] IoParamSpec[] inputs,
            [Description("Output params to build, in order: name + access + optional type hint.")] IoParamSpec[] outputs)
            => WithActivity(id, () => _bridge.SetIo(id, inputs ?? Array.Empty<IoParamSpec>(), outputs ?? Array.Empty<IoParamSpec>()));

        // --- Run + read ---

        public RunResult Run(
            [Description("InstanceGuid of the component to solve.")] Guid id)
            => WithActivity(id, () => _bridge.Run(id));

        public RuntimeReport ReadRuntimeErrors(
            [Description("InstanceGuid of the component to read messages + outputs from.")] Guid id,
            [Description("Also include the other components' runtime messages (default false).")] bool includeDocument = false)
            => _bridge.ReadRuntimeErrors(id, includeDocument);

        static string Require(string value, string name)
            => string.IsNullOrEmpty(value) ? throw new ArgumentException($"{name} is required.", name) : value;
    }
}
