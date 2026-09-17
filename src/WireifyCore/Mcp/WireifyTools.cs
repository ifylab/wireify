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
        readonly Func<string, WireifyCore.Hosting.AppSurfaceInfo>? _appInfo;
        readonly Func<string, string, WireifyCore.Hosting.ScaffoldAppResult>? _appScaffold;

        // The two-strikes leash, mechanical: consecutive failed mutations per component id (the
        // tools instance lives for the host lifetime). From the second consecutive failure the
        // error/result carries the LEASH line — advisory and in-band, never a refusal to work.
        readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _consecutiveFailures = new();

        public WireifyTools(
            IGrasshopperBridge bridge,
            Action<Guid, bool>? activity = null,
            Func<string, WireifyCore.Hosting.AppSurfaceInfo>? appInfo = null,
            Func<string, string, WireifyCore.Hosting.ScaffoldAppResult>? appScaffold = null)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _activity = activity;
            _appInfo = appInfo;
            _appScaffold = appScaffold;
        }

        /// <summary>The companion-app surface for THIS session's home: URL (with the per-run
        /// token), what exists in the home, declared counts. No bridge call — pure server-side
        /// reads, so it works whatever tab is in front.</summary>
        public WireifyCore.Hosting.AppSurfaceInfo GetAppInfo()
            => Guard("get_app_info", () =>
            {
                if (_appInfo is null)
                    throw new InvalidOperationException(
                        "this host serves no app surface — the companion app is unavailable here.");
                var home = WireifySessionContext.CurrentHomeId
                    ?? throw new InvalidOperationException(
                        "no session home on this request — get_app_info needs the session header a Connect-launched terminal carries (X-Wireify-Home).");
                return _appInfo(home);
            });

        /// <summary>Raise this session's companion-app folder to a ready state: stamp the .ify
        /// app kit (Wireify-owned, refreshed), seed <c>index.html</c> and <c>manifest.json</c>
        /// only when absent — existing agent/user files are never touched. Disk-only, no bridge
        /// call. Returns the surface info PLUS the action report (what was seeded vs left
        /// alone), so the caller can say truthfully what just happened.</summary>
        public WireifyCore.Hosting.ScaffoldAppResult ScaffoldApp(
            [Description("Page shape to seed when index.html does not exist yet: panel (control-panel starter, default) or report (live-report shape: hero viewport, KPI strip, views, controls drawer). An existing page is never replaced.")] string template = "panel")
            => Guard("scaffold_app", () =>
            {
                if (_appScaffold is null)
                    throw new InvalidOperationException(
                        "this host serves no app surface — the companion app is unavailable here.");
                if (template != "panel" && template != "report")
                    throw new ArgumentException(
                        $"template '{template}' is not a page shape — pass panel or report.", nameof(template));
                var home = WireifySessionContext.CurrentHomeId
                    ?? throw new InvalidOperationException(
                        "no session home on this request — scaffold_app needs the session header a Connect-launched terminal carries (X-Wireify-Home).");
                return _appScaffold(home, template);
            });

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

        public DocumentGraph GetDocumentGraph(
            [Description("Scope the read to these object ids (a component and its neighbours) — no cap applies; ids no object carries come back in missingIds. Omit for the whole definition.")] Guid[]? ids = null,
            [Description("Include each node's input/output params (names, access, type, hint, id) — default true. The wiring itself is always the edges list.")] bool includeParams = true,
            [Description("Also inline each output's live data (3 samples per branch, 12 total) — the values a port or an explanation needs, in the same call (default false).")] bool includeOutputs = false,
            [Description("Cap on the nodes for production-size canvases (default 300; <=0 = no cap). Selected and Wireify-managed objects are kept first; nodesTruncated + totalObjectCount report the cut.")] int maxComponents = SummaryBounding.DefaultMaxComponents,
            [Description("Case-insensitive substring filter on object name/nickname — a targeted subgraph instead of the whole canvas.")] string? nameFilter = null)
            => Guard("get_document_graph", () => _bridge.GetDocumentGraph(ids, includeParams, includeOutputs, maxComponents, nameFilter));

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
            [Description("Target runtime: CPython3 (default) or IronPython2.")] PythonRuntime runtime = PythonRuntime.CPython3,
            [Description("Nickname for the new component. Component nicknames are user-facing — the companion app renders them on every card and report heading, and an unnamed component presents as the stock 'Py3'. Name it for what it computes.")] string? nickName = null)
            => Guard("create_python_component", () => _bridge.CreatePythonComponent(runtime, nickName));

        public AppControlState CreateControlComponent(
            [Description("What to create: kind (slider, panel, toggle, valuelist, button, mdslider, colour, or knob) plus that kind's configuration; nearId places it beside an existing object.")] ControlSpec spec)
            => Guard("create_control_component", () =>
            {
                if (spec is null) throw new ArgumentException("spec is required.", nameof(spec));
                return _bridge.CreateControlComponent(spec);
            });

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
                    // The result schema declares report required; a null would serialize away
                    // and hand every schema-validating client an invalid payload (round-4
                    // defect C). solve:false gets an honest placeholder instead.
                    report ??= new RuntimeReport(
                        new[] { new RuntimeMessage("remark", "compiled without solving (solve=false) — outputs are stale until a solve; call run to execute") },
                        Array.Empty<OutputValue>());
                    return new SetSourceResult(id, solve, report);
                });
            });

        public TypedIoResult SetTypedIo(
            [Description("InstanceGuid of the component whose params to (re)build from its script.")] Guid id)
            => Guard("set_typed_io", () => WithActivity(id, () => _bridge.SetParametersFromScript(id)));

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

        public ClearBadgeResult ClearBadge(
            [Description("InstanceGuid of the object whose wireify badge record to remove. Stale guids are accepted — clearing a record whose object is gone is cleanup, not an error.")] Guid id)
            => Guard("clear_badge", () => WithActivity(id, () => _bridge.ClearBadge(id)));

        public RenameResult RenameComponent(
            [Description("InstanceGuid of the object to rename: a native control (slider, panel, toggle, value list, button, MD slider, colour swatch, knob), a script component, or any native component. Wireify sockets are refused — they are addressed by number; convert first.")] Guid id,
            [Description("The new nickname — user-facing text: the companion app shows a control's nickname on its card and the report prints it as the row label. Keep a 'W<n> ' prefix when the user still addresses a converted component by number. Ignored when clear is true.")] string nickName = "",
            [Description("Un-name the object deliberately: the nickname becomes empty and its app card and report row read by kind again. The way back after a rename the user did not want — one undo record like any rename. Without it an empty nickName is refused.")] bool clear = false)
            => Guard("rename_component", () =>
            {
                if (!clear && string.IsNullOrWhiteSpace(nickName))
                    throw new ArgumentException(
                        "nickName must not be empty — names are the page's copy. To un-name deliberately, pass clear: true.",
                        nameof(nickName));
                var final = clear ? "" : nickName.Trim();
                return WithActivity(id, () => _bridge.RenameComponent(id, final));
            });

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
