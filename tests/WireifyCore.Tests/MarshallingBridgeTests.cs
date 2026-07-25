// SPDX-License-Identifier: Apache-2.0
using System;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class MarshallingBridgeTests
{
    sealed class RecordingInvoker : IUiInvoker
    {
        public int Calls;
        public T Invoke<T>(Func<T> func) { Calls++; return func(); }
        public void Invoke(Action action) { Calls++; action(); }
    }

    [Fact]
    public void Every_call_goes_through_the_ui_invoker()
    {
        var fake = new FakeBridge();
        var invoker = new RecordingInvoker();
        var bridge = new MarshallingBridge(fake, invoker);

        bridge.GetDocumentSummary();
        bridge.CreatePythonComponent(PythonRuntime.CPython3);
        bridge.SetSource(FakeBridge.SomeId, "x = 1", PythonRuntime.CPython3);
        bridge.Run(FakeBridge.SomeId);

        Assert.Equal(4, invoker.Calls);
        Assert.Contains("GetDocumentSummary:False:300:-", fake.Calls);
        Assert.Contains($"Run:{FakeBridge.SomeId}", fake.Calls);
    }

    [Fact]
    public void Results_propagate_back_through_the_invoker()
    {
        var bridge = new MarshallingBridge(new FakeBridge(), new InlineUiInvoker());

        Assert.Equal(FakeBridge.SomeId, bridge.CreatePythonComponent(PythonRuntime.CPython3));
    }

    [Fact]
    public void Wire_passes_the_mode_through_and_returns_the_receipt()
    {
        var fake = new FakeBridge();
        var bridge = new MarshallingBridge(fake, new InlineUiInvoker());

        var result = bridge.Wire(FakeBridge.SomeId, 0, FakeBridge.SomeId, 1, WireMode.Add);

        Assert.Contains($"Wire:{FakeBridge.SomeId}:0:{FakeBridge.SomeId}:1:Add", fake.Calls);
        Assert.Equal("add", result.Mode);
    }

    sealed class SlowInvoker : IUiInvoker
    {
        readonly int _delayMs;
        public int MaxConcurrency;
        int _current;
        public SlowInvoker(int delayMs) => _delayMs = delayMs;

        public T Invoke<T>(Func<T> func)
        {
            var now = System.Threading.Interlocked.Increment(ref _current);
            if (now > MaxConcurrency) MaxConcurrency = now;
            try { System.Threading.Thread.Sleep(_delayMs); return func(); }
            finally { System.Threading.Interlocked.Decrement(ref _current); }
        }

        public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });
    }

    [Fact]
    public void Concurrent_calls_serialize_through_the_gate()
    {
        var invoker = new SlowInvoker(100);
        var bridge = new MarshallingBridge(new FakeBridge(), invoker);

        var t1 = System.Threading.Tasks.Task.Run(() => bridge.GetRuntimeInfo());
        var t2 = System.Threading.Tasks.Task.Run(() => bridge.GetRuntimeInfo());
        System.Threading.Tasks.Task.WaitAll(t1, t2);

        Assert.Equal(1, invoker.MaxConcurrency);
    }

    [Fact]
    public void Queued_call_times_out_with_a_clear_message_instead_of_hanging()
    {
        var logs = new List<(string Message, bool Ok)>();
        var bridge = new MarshallingBridge(new FakeBridge(), new SlowInvoker(500),
            (m, ok) => { lock (logs) logs.Add((m, ok)); },
            queueTimeout: TimeSpan.FromMilliseconds(50));

        var holder = System.Threading.Tasks.Task.Run(() => bridge.GetRuntimeInfo());
        System.Threading.Thread.Sleep(100); // let the holder take the gate

        var ex = Assert.Throws<TimeoutException>(() => bridge.GetDocumentSummary());
        Assert.Contains("NOT started", ex.Message);
        Assert.Contains("WIREIFY_QUEUE_TIMEOUT", ex.Message); // the stable code the loop skill keys on
        holder.Wait();

        lock (logs) Assert.Contains(logs, l => !l.Ok && l.Message.Contains("get_document_summary"));
    }

    [Fact]
    public void Every_call_logs_tool_name_duration_and_outcome()
    {
        var logs = new List<(string Message, bool Ok)>();
        var bridge = new MarshallingBridge(new FakeBridge(), new InlineUiInvoker(), (m, ok) => logs.Add((m, ok)));

        bridge.GetRuntimeInfo();

        Assert.Contains(logs, l => l.Ok && l.Message.StartsWith("get_runtime_info ok in"));
    }

    [Fact]
    public void Entry_log_fires_before_the_body_so_a_dead_call_still_leaves_a_trace()
    {
        var entries = new List<string>();
        var fake = new FakeBridge { SetIoThrows = new InvalidOperationException("boom") };
        var bridge = new MarshallingBridge(fake, new InlineUiInvoker(), entryLog: entries.Add);

        bridge.GetRuntimeInfo();
        Assert.Throws<InvalidOperationException>(() =>
            bridge.SetIo(FakeBridge.SomeId, Array.Empty<IoParamSpec>(), Array.Empty<IoParamSpec>()));

        // The failing call is on record by name — the outcome line alone cannot distinguish
        // "never returned" from "returned and the client lost it".
        Assert.Equal(new[] { "get_runtime_info", "set_io" }, entries);
    }

    [Fact]
    public void Session_context_is_snapshotted_into_the_call_and_cleared_after()
    {
        var sink = new List<string?>();
        var bridge = new MarshallingBridge(new FakeBridge(), new InlineUiInvoker(),
            callContext: sink.Add);

        WireifySessionContext.CurrentHomeId = "tower-a1b2c3d4";
        try { bridge.GetRuntimeInfo(); }
        finally { WireifySessionContext.CurrentHomeId = null; }
        bridge.GetRuntimeInfo(); // no ambient session — the slot gets null for the call too

        // set-before / clear-after per call: the resolver's slot never leaks across calls.
        Assert.Equal(new string?[] { "tower-a1b2c3d4", null, null, null }, sink);
    }
}
