// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Linq;
using WireifyCore.Bridge;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class UsageLedgerTests
{
    const string Home = "tower-a1b2c3d4";

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-usage-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    sealed class Clock
    {
        public DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        public DateTime Tick(int minutes = 1) => Now = Now.AddMinutes(minutes);
    }

    static UsageLedger.Record Load(string dir) => UsageLedger.Read(UsageLedger.PathFor(dir, Home))!;

    [Fact]
    public void Calls_count_per_tool_and_only_successful_structural_calls_count_as_mutations()
    {
        var dir = TempDir();
        var ledger = new UsageLedger(dir);

        ledger.Observe(new CallObservation("get_document_summary", true, 12, Home));
        ledger.Observe(new CallObservation("set_source", true, 40, Home));
        ledger.Observe(new CallObservation("set_source", false, 8, Home));
        ledger.Observe(new CallObservation("wire", true, 5, Home));

        var record = Load(dir);
        Assert.Equal(4, record.Calls);
        Assert.Equal(1, record.Failures);
        Assert.Equal(2, record.Mutations);
        Assert.Equal(2, record.Tools["set_source"].Calls);
        Assert.Equal(1, record.Tools["set_source"].Failures);
        Assert.Equal(48, record.Tools["set_source"].TotalMs);
        var session = Assert.Single(record.Sessions);
        Assert.Equal(4, session.Calls);
        Assert.Equal(2, session.Mutations);
        Assert.Null(session.EndUtc);
    }

    [Fact]
    public void Calls_without_a_session_and_companion_app_calls_are_not_recorded()
    {
        var dir = TempDir();
        var ledger = new UsageLedger(dir);

        ledger.Observe(new CallObservation("set_source", true, 1, null));
        ledger.Observe(new CallObservation("set_app_control_value", true, 1, Home, AppSurface: true));

        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Ending_a_session_closes_its_row_and_the_next_call_opens_a_new_one()
    {
        var dir = TempDir();
        var clock = new Clock();
        var ledger = new UsageLedger(dir, () => clock.Now);

        ledger.Observe(new CallObservation("wire", true, 1, Home));
        clock.Tick(5);
        ledger.Observe(new CallObservation("run", true, 1, Home));
        var lastCall = clock.Now;
        clock.Tick(30);
        ledger.EndSession(Home);
        ledger.EndSession(Home); // nothing open: no change
        clock.Tick();
        ledger.Observe(new CallObservation("run", true, 1, Home));

        var record = Load(dir);
        Assert.Equal(2, record.SessionCount);
        Assert.Equal(2, record.Sessions.Count);
        Assert.Equal(lastCall, record.Sessions[0].EndUtc); // ends at its last call, not at the close
        Assert.Null(record.Sessions[1].EndUtc);
    }

    [Fact]
    public void Session_rows_keep_the_newest_twenty_while_the_count_keeps_everything()
    {
        var dir = TempDir();
        var ledger = new UsageLedger(dir);

        for (var i = 0; i < UsageLedger.MaxSessions + 5; i++)
        {
            ledger.Observe(new CallObservation(i % 2 == 0 ? "wire" : "run", true, 1, Home));
            ledger.EndSession(Home);
        }

        var record = Load(dir);
        Assert.Equal(UsageLedger.MaxSessions, record.Sessions.Count);
        Assert.Equal(UsageLedger.MaxSessions + 5, record.SessionCount);
    }

    [Fact]
    public void A_new_ledger_continues_the_file_on_disk()
    {
        var dir = TempDir();
        new UsageLedger(dir).Observe(new CallObservation("wire", true, 1, Home));

        new UsageLedger(dir).Observe(new CallObservation("wire", true, 1, Home));

        var record = Load(dir);
        Assert.Equal(2, record.Calls);
        Assert.Single(record.Sessions);
    }

    [Fact]
    public void A_write_failure_never_throws_and_is_reported_once()
    {
        var dir = TempDir();
        var blocked = Path.Combine(dir, "usage");
        File.WriteAllText(blocked, "a file where the directory should be");
        var errors = 0;
        var ledger = new UsageLedger(blocked, onError: _ => errors++);

        ledger.Observe(new CallObservation("wire", true, 1, Home));
        ledger.Observe(new CallObservation("wire", true, 1, Home));
        ledger.EndSession(Home);

        Assert.Equal(1, errors);
    }

    [Fact]
    public void Unreadable_file_reads_as_a_new_home()
    {
        var dir = TempDir();
        File.WriteAllText(UsageLedger.PathFor(dir, Home), "{ not json");

        Assert.Null(UsageLedger.Read(UsageLedger.PathFor(dir, Home)));
        new UsageLedger(dir).Observe(new CallObservation("wire", true, 1, Home));
        Assert.Equal(1, Load(dir).Calls);
    }

    [Fact]
    public void Mutation_set_matches_the_lesson_gate_matcher()
    {
        // The matcher text lives in settings.json.tmpl; LessonGateHookTests pins it verbatim.
        Assert.Equal(
            new[] { "convert_staged", "create_python_component", "set_io", "set_source", "set_typed_io", "wire" },
            UsageLedger.MutationTools.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Warmup_names_the_previous_session_and_the_last_lesson()
    {
        var dir = TempDir();
        var clock = new Clock();
        var ledger = new UsageLedger(dir, () => clock.Now);
        ledger.Observe(new CallObservation("set_source", true, 1, Home));
        clock.Tick(14);
        ledger.Observe(new CallObservation("wire", true, 1, Home));
        ledger.EndSession(Home);

        var text = UsageLedger.WarmupText(Load(dir), new MemoryLedger.LedgerStats(3, "2026-10-20"));

        Assert.Equal(
            "Previous session on this definition: 2 wireify mutations, last at 2026-10-28 09:14 UTC; last recorded lesson: 2026-10-20.\n"
            + "If that session ended mid-task, orient with get_document_summary before resuming.\n",
            text);
        Assert.Contains("last recorded lesson: none yet.",
            UsageLedger.WarmupText(Load(dir), new MemoryLedger.LedgerStats(0, null)));
    }

    [Fact]
    public void No_previous_session_means_no_warmup()
    {
        Assert.Null(UsageLedger.WarmupText(null, null));
        Assert.Null(UsageLedger.WarmupText(new UsageLedger.Record(), new MemoryLedger.LedgerStats(2, "2026-10-01")));
    }
}
