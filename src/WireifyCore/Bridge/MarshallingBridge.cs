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
        readonly TimeSpan _queueTimeout;
        readonly object _gate = new();

        public MarshallingBridge(
            IGrasshopperBridge inner, IUiInvoker ui,
            Action<string, bool>? log = null, TimeSpan? queueTimeout = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _log = log;
            _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(20);
        }

        T Call<T>(string tool, Func<T> body)
        {
            if (!Monitor.TryEnter(_gate, _queueTimeout))
            {
                var msg = $"{tool}: another wireify call has held Grasshopper for over " +
                          $"{_queueTimeout.TotalSeconds:0}s (a long solve, or Rhino is blocked); this call was NOT started. " +
                          "Wait for the canvas to settle, then retry once.";
                _log?.Invoke(msg, false);
                throw new TimeoutException(msg);
            }
            var sw = Stopwatch.StartNew();
            try
            {
                var result = _ui.Invoke(body);
                _log?.Invoke($"{tool} ok in {sw.ElapsedMilliseconds}ms", true);
                return result;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{tool} failed after {sw.ElapsedMilliseconds}ms: {ex.Message}", false);
                throw;
            }
            finally { Monitor.Exit(_gate); }
        }

        public DocumentSummary GetDocumentSummary(bool includeStagedData = false)
            => Call("get_document_summary", () => _inner.GetDocumentSummary(includeStagedData));

        public ComponentIntrospection IntrospectComponent(Guid id)
            => Call("introspect_component", () => _inner.IntrospectComponent(id));

        public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
            => Call("introspect_selected", () => _inner.IntrospectSelected());

        public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50)
            => Call("read_input_data", () => _inner.ReadInputData(id, inputParam, maxPerBranch, maxTotal));

        public RuntimeInfo GetRuntimeInfo()
            => Call("get_runtime_info", () => _inner.GetRuntimeInfo());

        public ScriptSource GetSource(Guid id)
            => Call("get_source", () => _inner.GetSource(id));

        public Guid CreatePythonComponent(PythonRuntime runtime)
            => Call("create_python_component", () => _inner.CreatePythonComponent(runtime));

        public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true)
            => Call("set_source", () => _inner.SetSource(id, source, runtime, solve));

        public void SetParametersFromScript(Guid id)
            => Call<object?>("set_typed_io", () => { _inner.SetParametersFromScript(id); return null; });

        public void Wire(Guid fromId, int fromOutput, Guid toId, int toInput)
            => Call<object?>("wire", () => { _inner.Wire(fromId, fromOutput, toId, toInput); return null; });

        public ConvertStagedResult ConvertStaged(
            Guid socketId, string code, IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime, string? nicknameSlug, IReadOnlyList<IoParamSpec>? inputs)
            => Call("convert_staged", () => _inner.ConvertStaged(socketId, code, outputs, runtime, nicknameSlug, inputs));

        public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs)
            => Call("set_io", () => _inner.SetIo(id, inputs, outputs));

        public RunResult Run(Guid id)
            => Call("run", () => _inner.Run(id));

        public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false)
            => Call("read_runtime_errors", () => _inner.ReadRuntimeErrors(id, includeDocument));
    }
}
