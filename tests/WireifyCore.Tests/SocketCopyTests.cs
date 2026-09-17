// SPDX-License-Identifier: Apache-2.0
using WireifyContract;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>The socket plate's copy (round-8 §11 + the design call): what the two capsules say,
/// when they act, and which lines sit between them — pure, so the .gha only paints.</summary>
public class SocketCopyTests
{
    static WireifyAppStatus Page(bool exists) => new(
        true, exists, "truss-4086bab4", "127.0.0.1:9473/app/truss-4086bab4", "/home/app",
        exists ? "" : "no app page yet");

    [Fact]
    public void Idle_socket_teaches_build_and_the_prompt()
    {
        var t = SocketCopy.Compose(new SocketView
        {
            Number = 2, InputNames = new[] { "areas", "pts" }, InputWired = new[] { true, true },
        });

        Assert.Equal("Build", t.BuildLabel);
        Assert.True(t.BuildActive);
        Assert.Equal("no app yet", t.AppLabel);
        Assert.False(t.AppActive);
        Assert.Equal("", t.Address);
        Assert.Equal(
            new[] { SocketCopy.IdleLine, "Then type: do #2: <what to compute from areas, pts>", SocketCopy.AppPageLine },
            t.Lines);
        Assert.All(t.Warm, w => Assert.False(w));
    }

    [Fact]
    public void Open_session_makes_build_inert_and_points_at_the_terminal()
    {
        var t = SocketCopy.Compose(new SocketView { SessionOpen = true });

        Assert.Equal("Session open", t.BuildLabel);
        Assert.False(t.BuildActive);
        Assert.Equal(SocketCopy.SessionLine, t.Lines[0]);
        Assert.StartsWith("In it: do #1:", t.Lines[1]);
        Assert.Equal(SocketCopy.AppPageLine, t.Lines[2]);
    }

    [Fact]
    public void A_page_puts_the_address_first_and_flips_the_app_capsule()
    {
        var t = SocketCopy.Compose(new SocketView { App = Page(true) });

        Assert.Equal("127.0.0.1:9473/app/truss-4086bab4", t.Address);
        Assert.Equal("Open app", t.AppLabel);
        Assert.True(t.AppActive);
        Assert.Equal(new[] { SocketCopy.CopyHintLine, SocketCopy.UseAppIdleLine }, t.Lines);

        var live = SocketCopy.Compose(new SocketView { App = Page(true), SessionOpen = true });
        Assert.Equal(SocketCopy.UseAppSessionLine, live.Lines[1]);
        Assert.Equal("Session open", live.BuildLabel);
    }

    [Fact]
    public void Building_shows_the_step_and_the_approval_reminder()
    {
        var t = SocketCopy.Compose(new SocketView { Building = true, BuildStep = "checking Claude Code" });
        Assert.False(t.BuildActive);
        Assert.Equal(new[] { "checking Claude Code", SocketCopy.ApproveLine }, t.Lines);

        var fresh = SocketCopy.Compose(new SocketView { Building = true });
        Assert.Equal(SocketCopy.StartingLine, fresh.Lines[0]);
    }

    [Fact]
    public void A_failed_build_stays_warm_with_its_hint_and_build_stays_clickable()
    {
        var t = SocketCopy.Compose(new SocketView
        {
            BuildError = "Claude Code not found", BuildHint = "install Claude Code, then Build again",
        });

        Assert.Equal(new[] { "Claude Code not found", "install Claude Code, then Build again" }, t.Lines);
        Assert.All(t.Warm, w => Assert.True(w));
        Assert.True(t.BuildActive);
    }

    [Fact]
    public void A_segmented_hint_becomes_up_to_three_warm_plate_lines_host_platform_first()
    {
        // Round-10 S10.9: one long sentence clipped at the plate's width lost the Windows
        // install line to a macOS curl pipe. The plate draws the first three segments.
        var hint = string.Join(SocketCopy.HintSeparator, new[] { "Install it:", "PowerShell: irm …", "then run claude once", "macOS: curl …", "needs a plan" });
        var t = SocketCopy.Compose(new SocketView { BuildError = "Claude Code CLI not found on PATH", BuildHint = hint });

        Assert.Equal(new[] { "Claude Code CLI not found on PATH", "Install it:", "PowerShell: irm …", "then run claude once" }, t.Lines);
        Assert.All(t.Warm, w => Assert.True(w));
        Assert.Equal(new[] { "Install it:", "PowerShell: irm …", "then run claude once" }, SocketCopy.HintLines(hint));
        Assert.Equal(new[] { "one plain hint" }, SocketCopy.HintLines("one plain hint"));
        Assert.Empty(SocketCopy.HintLines(""));
    }

    [Fact]
    public void A_transient_replaces_the_first_line_only()
    {
        var t = SocketCopy.Compose(new SocketView { App = Page(true), Transient = "link copied" });
        Assert.Equal("link copied", t.Lines[0]);
        Assert.Equal(SocketCopy.UseAppIdleLine, t.Lines[1]);
        Assert.False(t.Warm[0]);

        var warm = SocketCopy.Compose(new SocketView { Transient = "save the definition first", TransientWarm = true });
        Assert.Equal("save the definition first", warm.Lines[0]);
        Assert.True(warm.Warm[0]);
    }

    [Theory]
    [InlineData(new[] { "in1", "in2" }, new[] { true, true }, "do #1: <what to compute from in1, in2>")]
    [InlineData(new[] { "in1", "pts" }, new[] { false, true }, "do #1: <what to compute from pts>")]
    [InlineData(new[] { "in1" }, new[] { false }, "do #1: <what to make from the inputs you wire in>")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { true, true, true, true }, "do #1: <what to compute from a, b, c, …>")]
    public void The_prompt_line_follows_the_wires(string[] names, bool[] wired, string expected)
        => Assert.Equal(expected, SocketCopy.DoLine(new SocketView { InputNames = names, InputWired = wired }));
}
