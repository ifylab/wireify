// SPDX-License-Identifier: Apache-2.0
using System;
using System.Reflection;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class ExceptionUnwrapTests
{
    [Fact]
    public void Plain_exceptions_come_back_unchanged()
    {
        var ex = new InvalidOperationException("boom");
        Assert.Same(ex, ExceptionUnwrap.Innermost(ex));
    }

    [Fact]
    public void Reflection_and_task_wrappers_unwrap_to_the_innermost_cause()
    {
        var real = new TimeoutException("rebuild wedged");
        var wrapped = new TargetInvocationException(
            new AggregateException(new TargetInvocationException(real)));

        Assert.Same(real, ExceptionUnwrap.Innermost(wrapped));
    }

    [Fact]
    public void Aggregate_without_inners_stops_the_walk()
    {
        var agg = new AggregateException();
        Assert.Same(agg, ExceptionUnwrap.Innermost(agg));
    }

    [Fact]
    public void Compact_stack_is_empty_for_unthrown_exceptions()
    {
        Assert.Equal("", ExceptionUnwrap.CompactStack(new InvalidOperationException("no stack")));
    }

    [Fact]
    public void Compact_stack_flattens_thrown_frames_to_one_line()
    {
        Exception caught;
        try { ThrowDeep(); throw new InvalidOperationException("unreachable"); }
        catch (Exception ex) { caught = ex; }

        var stack = ExceptionUnwrap.CompactStack(caught, frames: 2);

        Assert.Contains("ThrowDeep", stack);
        Assert.DoesNotContain('\n', stack);
    }

    static void ThrowDeep() => throw new InvalidOperationException("deep");
}
