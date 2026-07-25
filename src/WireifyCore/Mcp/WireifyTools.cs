// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol;
using WireifyCore.Bridge;

namespace WireifyCore.Mcp
{
    /// <summary>
    /// The MCP tool surface: thin, input-validated delegation to <see cref="IGrasshopperBridge"/>,
    /// one method per tool. Parameter <see cref="DescriptionAttribute"/>s feed the generated input
    /// schema; the DTO return types document the output schema. Tool names + descriptions live in
    /// <see cref="WireifyToolRegistry"/>. Designed Tool-Search / code-execution friendly per the build plan.
    ///
    /// Every method runs inside <see cref="Guard{T}"/>: the SDK masks any non-McpException into
    /// "An error occurred invoking 'x'.", so real failures are unwrapped (reflection and task
    /// wrappers stripped) and rethrown as <see cref="McpException"/> — the sanctioned channel for
    /// detailed tool errors — naming the tool, the exception type, and the message.
    /// </summary>
    public sealed class WireifyTools
    {
        readonly IGrasshopperBridge _bridge;
        readonly Action<Guid, bool>? _activity;

        // The two-strikes leash, mechanical: consecutive failed mutations per component id (the
        // tools instance lives for the host lifetime). From the second consecutive failure the
        // error/result carries the LEASH line — advisory and in-band, never a refusal to work.
        readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _consecutiveFailures = new();

        public WireifyTools(IGrasshopperBridge bridge, Action<Guid, bool>? activity = null)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _activity = activity;
        }

        int BumpFailure(Guid id) => _consecutiveFailures.AddOrUpdate(id, 1, (_, n) => n + 1);
        void ResetFailures(Guid id) => _consecutiveFailures.TryRemove(id, out _);

        /// <summary>Wrap a bridge mutation failure in the Guard's message format, appending the
        /// LEASH line from the second consecutive failure on this component. Returns an
        /// McpException so the outer <see cref="Guard{T}"/> passes it through untouched.</summary>
        McpException LeashedFailure(string tool, Guid id, Exception ex)
        {
            var strikes = BumpFailure(id);
            var real = ExceptionUnwrap.Innermost(ex);
            var msg = $"{tool} failed — {real.GetType().Name}: {real.Message}";
            if (strikes >= 2) msg += "\n" + ErrorProtocol.LeashLine;
            return new McpException(msg, real);
        }

        /// <summary>Signals begin/end of a mutating tool call on a component, so the socket's
        /// attributes can show a live "Working" state while Claude edits it.</summary>
        T WithActivity<T>(Guid id, Func<T> work)
        {
            _activity?.Invoke(id, true);
            try { return work(); }
            finally { _activity?.Invoke(id, false); }
        }

        static T Guard<T>(string tool, Func<T> body)
        {
            try { return body(); }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                var real = ExceptionUnwrap.Innermost(ex);
                throw new McpException($"{tool} failed — {real.GetType().Name}: {real.Message}", real);
            }
        }

        static void Guard(string tool, Action body)
            => Guard<object?>(tool, () => { body(); return null; });

        // --- Orientation (read-only) ---

        public DocumentSummary GetDocumentSummary(
            [Description("Also inline the live data on each staged socket's wired inputs (same shape + caps as read_input_data) — orientation for a 'do #n' task in one call (default false).")] bool includeStagedData = false,
            [Description("Cap on the components list for production-size canvases (default 300; <=0 = no cap). Selected and Wireify-managed objects are kept first; componentsTruncated + totalObjectCount report the cut. The wireify registry is never truncated.")] int maxComponents = SummaryBounding.DefaultMaxComponents,
            [Description("Case-insensitive substring filter on component name/nickname — targeted lookup instead of re-listing a big canvas.")] string? nameFilter = null)
            => Guard("get_document_summary", () => _bridge.GetDocumentSummary(includeStagedData, maxComponents, nameFilter));

        public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
            => Guard("introspect_selected", () => _bridge.IntrospectSelected());

        public ComponentIntrospection IntrospectComponent(
            [Description("InstanceGuid of the component (or floating param, e.g. a panel or slider) to introspect.")] Guid id)
            => Guard("introspect_component", () => _bridge.IntrospectComponent(id));

        public InputData ReadInputData(
            [Description("InstanceGuid of the component that owns the input.")] Guid id,
            [Description("Name or nickname of the input parameter to read.")] string inputParam,
            [Description("Max samples per data-tree branch (default 5).")] int maxPerBranch = 5,
            [Description("Max samples total across all branches (default 50).")] int maxTotal = 50)
            => Guard("read_input_data", () =>
                _bridge.ReadInputData(id, Require(inputParam, nameof(inputParam)), maxPerBranch, maxTotal));

        public RuntimeInfo GetRuntimeInfo()
            => Guard("get_runtime_info", () => _bridge.GetRuntimeInfo());

        public ScriptSource GetSource(
            [Description("InstanceGuid of the script component whose source to read.")] Guid id)
            => Guard("get_source", () => _bridge.GetSource(id));

        // --- Build (mutation) ---

        public Guid CreatePythonComponent(
            [Description("Target runtime: CPython3 (default) or IronPython2.")] PythonRuntime runtime = PythonRuntime.CPython3)
            => Guard("create_python_component", () => _bridge.CreatePythonComponent(runtime));

        public SetSourceResult SetSource(
            [Description("InstanceGuid of the target Python component.")] Guid id,
            [Description("Python source to inject and compile.")] string source,
            [Description("Runtime the source targets (default CPython3).")] PythonRuntime runtime = PythonRuntime.CPython3,
            [Description("Solve after compiling and return the runtime report (default true). Pass false on heavy canvases and use run instead.")] bool solve = true,
            [Description("Overwrite even when the component was hand-edited outside Wireify since the last write (default false: such a write refuses with WIREIFY_EXTERNAL_EDIT and embeds the current code to merge). Pass true only after the user explicitly approves discarding their edits.")] bool overwriteExternalEdits = false)
            => Guard("set_source", () =>
            {
                var validated = Require(source, nameof(source));
                return WithActivity(id, () =>
                {
                    var report = _bridge.SetSource(id, validated, runtime, solve, overwriteExternalEdits);
                    return new SetSourceResult(id, solve, report);
                });
            });

        public Guid SetTypedIo(
            [Description("InstanceGuid of the component whose params to (re)build from its script.")] Guid id)
            => Guard("set_typed_io", () => WithActivity(id, () =>
            {
                _bridge.SetParametersFromScript(id);
                return id;
            }));

        public WireResult Wire(
            [Description("InstanceGuid of the upstream (source) component.")] Guid fromId,
            [Description("Zero-based output index on the upstream component.")] int fromOutput,
            [Description("InstanceGuid of the downstream (target) component.")] Guid toId,
            [Description("Zero-based input index on the downstream component.")] int toInput,
            [Description("How to treat a target input that already has sources: Strict (default) refuses without touching the document; Replace swaps the existing wire(s) out (one undo); Add merges deliberately (branches combine).")] WireMode mode = WireMode.Strict)
            => Guard("wire", () => _bridge.Wire(fromId, fromOutput, toId, toInput, mode));

        public ConvertStagedResult ConvertStaged(
            [Description("InstanceGuid of the staged Wireify socket to convert.")] Guid id,
            [Description("Plain script-mode Python: read the staged input names as variables, assign each declared output.")] string code,
            [Description("The output params to build, in order: name + access (+ optional type hint). Required — outputs are never derived from source.")] IoParamSpec[] outputs,
            [Description("Target runtime (default CPython3).")] PythonRuntime runtime = PythonRuntime.CPython3,
            [Description("Short kebab-case task slug for the nickname, e.g. 'cull-panels' -> 'W3 cull-panels'.")] string? nicknameSlug = null,
            [Description("Access (+ optional hint) per staged input, matched by name; must cover every WIRED staged input — an unwired staged input is dropped from the built component unless declared here. Omit for all wired inputs as tree, no hints.")] IoParamSpec[]? inputs = null)
            => Guard("convert_staged", () =>
            {
                var validated = Require(code, nameof(code));
                if (outputs is null || outputs.Length == 0)
                    throw new ArgumentException("outputs is required.", nameof(outputs));
                return WithActivity(id, () =>
                {
                    ConvertStagedResult result;
                    try { result = _bridge.ConvertStaged(id, validated, outputs, runtime, nicknameSlug, inputs); }
                    catch (McpException) { throw; }
                    catch (Exception ex) { throw LeashedFailure("convert_staged", id, ex); }

                    if (result.Converted)
                    {
                        ResetFailures(id);
                        return result;
                    }
                    // A refusal is a failed mutation attempt too — same counter, LEASH into Error.
                    return BumpFailure(id) >= 2
                        ? result with { Error = result.Error + "\n" + ErrorProtocol.LeashLine }
                        : result;
                });
            });

        public ComponentIntrospection SetIo(
            [Description("InstanceGuid of the script component whose I/O to define.")] Guid id,
            [Description("Input params to build, in order: name + access (item/list/tree) + optional type hint.")] IoParamSpec[] inputs,
            [Description("Output params to build, in order: name + access + optional type hint.")] IoParamSpec[] outputs)
            => Guard("set_io", () => WithActivity(id, () =>
            {
                try
                {
                    var result = _bridge.SetIo(id, inputs ?? Array.Empty<IoParamSpec>(), outputs ?? Array.Empty<IoParamSpec>());
                    ResetFailures(id);
                    return result;
                }
                catch (McpException) { throw; }
                catch (Exception ex) { throw LeashedFailure("set_io", id, ex); }
            }));

        public DeletedComponent DeleteComponent(
            [Description("InstanceGuid of the Wireify-managed object to delete: a Wireify socket or a script component. Anything else is refused — remove it manually in Grasshopper.")] Guid id)
            => Guard("delete_component", () => WithActivity(id, () => _bridge.DeleteComponent(id)));

        public PanelText SetPanelText(
            [Description("InstanceGuid of the Panel component to write into.")] Guid id,
            [Description("The text the panel should hold — e.g. a file path feeding a Read File component.")] string text)
            => Guard("set_panel_text", () =>
            {
                var validated = Require(text, nameof(text));
                return WithActivity(id, () => _bridge.SetPanelText(id, validated));
            });

        // --- Run + read ---

        public RunResult Run(
            [Description("InstanceGuid of the component to solve.")] Guid id)
            => Guard("run", () => WithActivity(id, () => _bridge.Run(id)));

        public RuntimeReport ReadRuntimeErrors(
            [Description("InstanceGuid of the component to read messages + outputs from.")] Guid id,
            [Description("Also include the other components' runtime messages (default false).")] bool includeDocument = false)
            => Guard("read_runtime_errors", () => _bridge.ReadRuntimeErrors(id, includeDocument));

        static string Require(string value, string name)
            => string.IsNullOrEmpty(value) ? throw new ArgumentException($"{name} is required.", name) : value;
    }
}
