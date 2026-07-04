// SPDX-License-Identifier: Apache-2.0
using System.IO;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class ConnectSupportTests
{
    [Fact]
    public void HomeId_is_stable_and_path_specific()
    {
        var a1 = WireifyPaths.HomeId("/projects/tower.gh");
        var a2 = WireifyPaths.HomeId("/projects/tower.gh");
        var other = WireifyPaths.HomeId("/projects/other.gh");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, other);
        Assert.StartsWith("tower-", a1);
    }

    [Fact]
    public void HomeId_handles_empty_path()
    {
        Assert.Equal("untitled", WireifyPaths.HomeId(""));
    }

    [Fact]
    public void Preflight_finds_claude_on_a_given_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-pre-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "claude"), "#!/bin/sh");

        var result = Preflight.CheckClaude(pathEnv: dir, windows: false);

        Assert.True(result.ClaudeFound);
        Assert.Equal(Path.Combine(dir, "claude"), result.ClaudePath);
    }

    [Fact]
    public void Preflight_reports_missing_claude()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-pre-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var result = Preflight.CheckClaude(pathEnv: dir, windows: false);

        Assert.False(result.ClaudeFound);
        Assert.NotNull(result.Note);
    }
}
