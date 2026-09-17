// SPDX-License-Identifier: Apache-2.0
using System.Linq;
using WireifyContract;
using WireifyCore.Hosting;

namespace WireifyCore.Tests;

/// <summary>The last Build's steps per definition, for a panel opened after the fact
/// (round-10 S10.5: dashes above a log that listed every completed step).</summary>
public class StepLedgerTests
{
    static WireifyConnectStep Step(string kind, string message = "done")
        => new("[wireify]", message, true, kind);

    [Fact]
    public void Steps_raised_inside_a_builds_scope_are_kept_for_that_definition()
    {
        var ledger = new StepLedger();
        using (ledger.Begin(@"C:\data\HALO.gh"))
        {
            ledger.Record(Step("server", "server listening"));
            ledger.Record(Step("home", "home scaffolded"));
        }

        var steps = ledger.For(@"C:\data\HALO.gh");
        Assert.Equal(new[] { "server", "home" }, steps.Select(s => s.Kind));
        Assert.Empty(ledger.For(@"C:\data\other.gh"));
        Assert.Empty(ledger.For(null));
    }

    [Fact]
    public void A_later_build_replaces_the_earlier_one_and_steps_outside_a_scope_are_dropped()
    {
        var ledger = new StepLedger();
        using (ledger.Begin(@"C:\data\HALO.gh")) ledger.Record(Step("server"));
        ledger.Record(Step("terminal", "a terminal closed")); // not a Build step
        using (ledger.Begin(@"C:\data\HALO.gh"))
        {
            ledger.Record(Step("home"));
            ledger.Record(Step("config"));
        }

        Assert.Equal(new[] { "home", "config" }, ledger.For(@"c:\DATA\halo.gh").Select(s => s.Kind));
    }

    [Fact]
    public void The_ledger_is_bounded()
    {
        var ledger = new StepLedger();
        using (ledger.Begin("a.gh"))
            for (var i = 0; i < StepLedger.MaxSteps + 10; i++) ledger.Record(Step("home", $"step {i}"));

        Assert.Equal(StepLedger.MaxSteps, ledger.For("a.gh").Count);
    }
}
