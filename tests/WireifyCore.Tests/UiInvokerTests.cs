// SPDX-License-Identifier: Apache-2.0
using System;
using System.Threading;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class UiInvokerTests
{
    [Fact]
    public void Runs_inline_when_already_on_the_ui_thread()
    {
        var invoker = new BoundedUiInvoker(_ => throw new InvalidOperationException("must not post"), () => false);

        Assert.Equal(7, invoker.Invoke(() => 7));
    }

    [Fact]
    public void Posts_and_returns_the_result()
    {
        var invoker = new BoundedUiInvoker(action => action(), () => true);

        Assert.Equal("ok", invoker.Invoke(() => "ok"));
    }

    [Fact]
    public void Rethrows_the_callback_exception_on_the_caller()
    {
        var invoker = new BoundedUiInvoker(action => action(), () => true);

        Assert.Throws<InvalidOperationException>(() => invoker.Invoke<int>(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void Pickup_timeout_throws_busy_and_the_late_callback_never_executes()
    {
        Action? parked = null;
        var invoker = new BoundedUiInvoker(action => parked = action, () => true, TimeSpan.FromMilliseconds(50));
        var ran = false;

        var ex = Assert.Throws<TimeoutException>(() => invoker.Invoke(() => { ran = true; return 1; }));
        Assert.Contains("NOT executed", ex.Message);

        parked!(); // the UI thread finally gets to it — abandoned, must no-op
        Assert.False(ran);
    }

    [Fact]
    public void Long_execution_after_pickup_is_not_a_timeout()
    {
        // Picked up immediately (post runs on a worker), but execution outlasts the pickup window:
        // a legitimate long solve must complete, not report busy.
        var invoker = new BoundedUiInvoker(
            action => ThreadPool.QueueUserWorkItem(_ => action()),
            () => true,
            TimeSpan.FromMilliseconds(100));

        var result = invoker.Invoke(() => { Thread.Sleep(300); return 42; });

        Assert.Equal(42, result);
    }
}
