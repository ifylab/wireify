// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Decorates an <see cref="IGrasshopperBridge"/> so every call runs on the Grasshopper UI thread
    /// via an <see cref="IUiInvoker"/>. The build plan's load-bearing threading rule: the MCP host
    /// serves on background threads, but document reads, mutations, and solves must happen on the UI
    /// thread. The inner bridge stays UI-thread-agnostic; this is the single marshalling seam.
    ///
    /// It also owns the two operational guarantees added after the round-5 hang:
    /// - <b>One wireify call at a time.</b> Clients may batch calls; concurrent
    ///   <c>RhinoApp.InvokeAndWait</c>-style dispatch is the hazard, so calls serialize HERE. A call
    ///   that cannot take its turn within the queue timeout fails with a clear message instead of
    ///   hanging until the client gives up.
    /// - <b>Per-call transparency.</b> Every call logs its tool name, duration, and outcome through
    ///   the optional log delegate (surfaces in the Wireify panel log).
    /// </summary>
    public sealed class MarshallingBridge : IGrasshopperBridge
    {
        readonly IGrasshopperBridge _inner;
        readonly IUiInvoker _ui;
        readonly Action<string, bool>? _log;
        readonly Action<string>? _entryLog;
        readonly Action<SessionCallContext?>? _callContext;
        readonly TimeSpan _queueTimeout;
        readonly object _gate = new();

        public MarshallingBridge(
            IGrasshopperBridge inner, IUiInvoker ui,
            Action<string, bool>? log = null, TimeSpan? queueTimeout = null,
            Action<SessionCallContext?>? callContext = null, Action<string>? entryLog = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _log = log;
            _entryLog = entryLog;
            _callContext = callContext;
            _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(20);
        }

        T Call<T>(string tool, Func<T> body)
        {
            // Entry before anything can block or throw: a call that hangs or dies client-side
            // must still leave its name on record — the outcome line alone cannot distinguish
            // "never returned" from "returned and the client lost it".
            _entryLog?.Invoke(tool);
            // Snapshot the calling session BEFORE any thread hop — the async context is intact
            // here; the UI-thread closure below must not depend on it flowing further.
            var session = WireifySessionContext.Snapshot();
            if (!Monitor.TryEnter(_gate, _queueTimeout))
            {
                var msg = ErrorProtocol.QueueTimeout(tool, _queueTimeout.TotalSeconds);
                _log?.Invoke(msg, false);
                throw new TimeoutException(msg);
            }
            var sw = Stopwatch.StartNew();
            try
            {
                // Serialization makes the single routing slot race-free: exactly one call's
                // context exists at a time, set here and cleared before the gate releases.
                _callContext?.Invoke(session);
                var result = _ui.Invoke(body);
                _log?.Invoke($"{tool} ok in {sw.ElapsedMilliseconds}ms", true);
                return result;
            }
            catch (Exception ex)
            {
                var real = ExceptionUnwrap.Innermost(ex);
                _log?.Invoke($"{tool} failed after {sw.ElapsedMilliseconds}ms: {real.GetType().Name}: {real.Message}", false);
                var stack = ExceptionUnwrap.CompactStack(ex);
                if (stack.Length > 0) _log?.Invoke($"{tool} at: {stack}", false);
                throw;
            }
            finally
            {
                _callContext?.Invoke(null);
                Monitor.Exit(_gate);
            }
        }

        public DocumentSummary GetDocumentSummary(
            bool includeStagedData = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents,
            string? nameFilter = null)
            => Call("get_document_summary", () => _inner.GetDocumentSummary(includeStagedData, maxComponents, nameFilter));

        public ComponentIntrospection IntrospectComponent(Guid id)
            => Call("introspect_component", () => _inner.IntrospectComponent(id));

        public DocumentGraph GetDocumentGraph(
            IReadOnlyList<Guid>? ids = null,
            bool includeParams = true,
            bool includeOutputs = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents,
            string? nameFilter = null)
            => Call("get_document_graph", () => _inner.GetDocumentGraph(ids, includeParams, includeOutputs, maxComponents, nameFilter));

        public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
            => Call("introspect_selected", () => _inner.IntrospectSelected());

        public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50)
            => Call("read_input_data", () => _inner.ReadInputData(id, inputParam, maxPerBranch, maxTotal));

        public RuntimeInfo GetRuntimeInfo()
            => Call("get_runtime_info", () => _inner.GetRuntimeInfo());

        public ScriptSource GetSource(Guid id)
            => Call("get_source", () => _inner.GetSource(id));

        public Guid CreatePythonComponent(PythonRuntime runtime, string? nickName = null)
            => Call("create_python_component", () => _inner.CreatePythonComponent(runtime, nickName));

        public AppControlState CreateControlComponent(ControlSpec spec)
            => Call("create_control_component", () => _inner.CreateControlComponent(spec));

        public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true, bool overwriteExternalEdits = false)
            => Call("set_source", () => _inner.SetSource(id, source, runtime, solve, overwriteExternalEdits));

        public TypedIoResult SetParametersFromScript(Guid id)
            => Call("set_typed_io", () => _inner.SetParametersFromScript(id));

        public WireResult Wire(Guid fromId, int fromOutput, Guid toId, int toInput, WireMode mode = WireMode.Strict)
            => Call("wire", () => _inner.Wire(fromId, fromOutput, toId, toInput, mode));

        public ConvertStagedResult ConvertStaged(
            Guid socketId, string code, IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime, string? nicknameSlug, IReadOnlyList<IoParamSpec>? inputs)
            => Call("convert_staged", () => _inner.ConvertStaged(socketId, code, outputs, runtime, nicknameSlug, inputs));

        public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs)
            => Call("set_io", () => _inner.SetIo(id, inputs, outputs));

        public DeletedComponent DeleteComponent(Guid id)
            => Call("delete_component", () => _inner.DeleteComponent(id));

        public ClearBadgeResult ClearBadge(Guid id)
            => Call("clear_badge", () => _inner.ClearBadge(id));

        public RenameResult RenameComponent(Guid id, string nickName)
            => Call("rename_component", () => _inner.RenameComponent(id, nickName));

        public PanelText SetPanelText(Guid id, string text)
            => Call("set_panel_text", () => _inner.SetPanelText(id, text));

        public RunResult Run(Guid id)
            => Call("run", () => _inner.Run(id));

        public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false)
            => Call("read_runtime_errors", () => _inner.ReadRuntimeErrors(id, includeDocument));

        public AppState ReadAppState(AppQuery query)
            => Call("app_state", () => _inner.ReadAppState(query));

        public AppGeometry ReadAppGeometry(AppViewRef view)
            => Call("app_geometry", () => _inner.ReadAppGeometry(view));

        public AppSetResult SetAppControlValue(Guid id, AppPushValue value)
            => Call("app_set_value", () => _inner.SetAppControlValue(id, value));

        public AppControlState SetAppGesture(Guid id, bool open)
            => Call("app_gesture", () => _inner.SetAppGesture(id, open));

        public IDisposable SubscribeSolutionEnd(
            Func<AppQuery?> queryProvider, Action<AppState> onSolution, Action<string>? onClosed = null,
            Action? onSolveStart = null, Action<AppState>? onActiveChanged = null)
        {
            // The subscription call itself marshals like any other (UI thread + the serialized
            // gate); the returned unsubscriber marshals its dispose the same way — removing a
            // handler from the document's event list is document access too. The PROVIDER and the
            // CALLBACKS (onSolution, onClosed, onSolveStart, onActiveChanged) are the exception:
            // they run on the UI thread, unmarshalled, and must never re-enter this seam.
            var inner = Call("app_subscribe", () => _inner.SubscribeSolutionEnd(
                queryProvider, onSolution, onClosed, onSolveStart, onActiveChanged));
            return new MarshalledDisposable(
                () => Call<object?>("app_unsubscribe", () => { inner.Dispose(); return null; }));
        }

        sealed class MarshalledDisposable : IDisposable
        {
            Action? _dispose;
            public MarshalledDisposable(Action dispose) => _dispose = dispose;
            public void Dispose() =>
                System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
