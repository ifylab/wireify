// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

/// <summary>The stop-hook lesson gate: per-OS settings rendering, script scaffolding, and the
/// gate scripts' actual behavior. The shell variant native to the running OS is exercised for
/// real (sh on the dev Mac, powershell on the Windows CI runner); the other side's rendering
/// is still pinned structurally, so both commands stay covered somewhere on every run.</summary>
public class LessonGateHookTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-hooks-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string TemplateRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "home-template");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent!;
        }
        throw new DirectoryNotFoundException("home-template not found walking up from " + AppContext.BaseDirectory);
    }

    static string Scaffold(HomeScaffolder.HookShell shell, out string home)
    {
        home = TempDir();
        new HomeScaffolder(TemplateRoot(), shell)
            .ScaffoldHome(home, new HomeScaffolder.Substitutions(52801, "sek", "tower.gh"));
        return File.ReadAllText(Path.Combine(home, ".claude", "settings.json"));
    }

    static string HookCommand(string settingsJson, string eventName, int group = 0)
    {
        var root = JsonNode.Parse(settingsJson)!.AsObject();
        return root["hooks"]![eventName]![group]!["hooks"]![0]!["command"]!.GetValue<string>();
    }

    [Fact]
    public void Windows_rendering_targets_powershell_and_ps1()
    {
        var settings = Scaffold(HomeScaffolder.HookShell.Windows, out var home);

        foreach (var evt in new[] { "SessionStart", "PostToolUse", "Stop" })
        {
            var cmd = HookCommand(settings, evt);
            Assert.StartsWith("powershell -NoProfile -ExecutionPolicy Bypass -File \"", cmd);
            Assert.EndsWith(".ps1\"", cmd);
            Assert.Contains(Path.Combine(".wireify", "hooks"), cmd);
        }
        Assert.Contains(Path.GetFullPath(home), HookCommand(settings, "Stop"));
    }

    [Fact]
    public void Posix_rendering_targets_sh()
    {
        var settings = Scaffold(HomeScaffolder.HookShell.Posix, out _);

        foreach (var evt in new[] { "SessionStart", "PostToolUse", "Stop" })
        {
            var cmd = HookCommand(settings, evt);
            Assert.StartsWith("sh \"", cmd);
            Assert.EndsWith(".sh\"", cmd);
        }
    }

    [Fact]
    public void Matcher_covers_exactly_the_six_structural_mutation_tools()
    {
        var settings = Scaffold(HomeScaffolder.HookShell.Posix, out _);

        var matcher = JsonNode.Parse(settings)!["hooks"]!["PostToolUse"]![0]!["matcher"]!.GetValue<string>();
        Assert.Equal(
            "mcp__wireify__(convert_staged|set_source|set_io|set_typed_io|create_python_component|wire)",
            matcher);
        // Re-solve and transport tools stay out by design: running is not a structural change.
        Assert.DoesNotContain("run", matcher.Replace("create_python_component", ""));
        Assert.DoesNotContain("delete_component", matcher);
        Assert.DoesNotContain("set_panel_text", matcher);
    }

    [Fact]
    public void Hook_scripts_are_scaffolded_and_refreshed_every_connect()
    {
        Scaffold(HomeScaffolder.HookShell.Posix, out var home);
        var hooksDir = Path.Combine(home, ".wireify", "hooks");

        foreach (var name in new[] { "session-marker", "log-mutation", "stop-review" })
        {
            Assert.True(File.Exists(Path.Combine(hooksDir, name + ".sh")), name + ".sh missing");
            Assert.True(File.Exists(Path.Combine(hooksDir, name + ".ps1")), name + ".ps1 missing");
        }

        var stop = Path.Combine(hooksDir, "stop-review.sh");
        File.Delete(Path.Combine(hooksDir, "log-mutation.sh"));
        File.WriteAllText(stop, "tampered");

        new HomeScaffolder(TemplateRoot(), HomeScaffolder.HookShell.Posix)
            .ScaffoldHome(home, new HomeScaffolder.Substitutions(52801, "sek", "tower.gh"));

        Assert.True(File.Exists(Path.Combine(hooksDir, "log-mutation.sh")));
        Assert.NotEqual("tampered", File.ReadAllText(stop));
    }

    // ---- native-shell functional runs ---------------------------------------------------------

    sealed record HookRun(int ExitCode, string StdErr);

    static HookRun RunHook(string homeDir, string baseName, string stdin = "{}")
    {
        var hooksDir = Path.Combine(homeDir, ".wireify", "hooks");
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(hooksDir, baseName + ".ps1")}\"")
            : new ProcessStartInfo("/bin/sh", $"\"{Path.Combine(hooksDir, baseName + ".sh")}\"");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new HookRun(p.ExitCode, err);
    }

    /// <summary>A scaffolded home arranged into "mutations ran, no lesson recorded": the marker
    /// exists, mutations.log is newer than it, MEMORY.md older. Times are set explicitly and in
    /// the PAST so the shell's whole-second `-nt` comparison sees files the scripts create now
    /// (review-done) as strictly newer — matching real sessions, where the block fires minutes
    /// after the marker, never inside its second.</summary>
    static string BlockableHome()
    {
        Scaffold(HomeScaffolder.HookShell.Auto, out var home);
        var stateDir = Path.Combine(home, ".wireify");
        var t0 = DateTime.UtcNow.AddSeconds(-30);

        File.WriteAllText(Path.Combine(stateDir, "session-start-marker"), "");
        File.WriteAllText(Path.Combine(stateDir, "mutations.log"), "2026-07-27T00:00:00Z\n");
        File.SetLastWriteTimeUtc(Path.Combine(stateDir, "session-start-marker"), t0);
        File.SetLastWriteTimeUtc(Path.Combine(stateDir, "mutations.log"), t0.AddSeconds(5));
        File.SetLastWriteTimeUtc(Path.Combine(home, "MEMORY.md"), t0.AddSeconds(-5));
        return home;
    }

    [Fact]
    public void Stop_review_blocks_once_then_stays_quiet()
    {
        var home = BlockableHome();

        var first = RunHook(home, "stop-review");
        Assert.Equal(2, first.ExitCode);
        Assert.Contains("MEMORY.md", first.StdErr);
        Assert.Contains("nothing durable", first.StdErr);
        Assert.True(File.Exists(Path.Combine(home, ".wireify", "review-done")));

        var second = RunHook(home, "stop-review");
        Assert.Equal(0, second.ExitCode);
    }

    [Fact]
    public void Stop_review_passes_when_a_lesson_was_recorded()
    {
        var home = BlockableHome();
        File.SetLastWriteTimeUtc(Path.Combine(home, "MEMORY.md"), DateTime.UtcNow.AddSeconds(10));

        Assert.Equal(0, RunHook(home, "stop-review").ExitCode);
    }

    [Fact]
    public void Stop_review_honors_stop_hook_active()
    {
        var home = BlockableHome();

        var run = RunHook(home, "stop-review", "{\"stop_hook_active\":true}");
        Assert.Equal(0, run.ExitCode);
        Assert.False(File.Exists(Path.Combine(home, ".wireify", "review-done")));
    }

    [Fact]
    public void Stop_review_passes_a_read_only_session()
    {
        Scaffold(HomeScaffolder.HookShell.Auto, out var home);
        File.WriteAllText(Path.Combine(home, ".wireify", "session-start-marker"), "");

        Assert.Equal(0, RunHook(home, "stop-review").ExitCode);
    }

    [Fact]
    public void Stop_review_fails_open_without_a_marker()
    {
        var home = BlockableHome();
        File.Delete(Path.Combine(home, ".wireify", "session-start-marker"));

        Assert.Equal(0, RunHook(home, "stop-review").ExitCode);
    }

    [Fact]
    public void Session_marker_stamps_and_resets_the_session_state()
    {
        var home = BlockableHome();
        var stateDir = Path.Combine(home, ".wireify");
        File.WriteAllText(Path.Combine(stateDir, "review-done"), "");

        Assert.Equal(0, RunHook(home, "session-marker").ExitCode);

        Assert.True(File.Exists(Path.Combine(stateDir, "session-start-marker")));
        Assert.False(File.Exists(Path.Combine(stateDir, "mutations.log")));
        Assert.False(File.Exists(Path.Combine(stateDir, "review-done")));
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(stateDir, "session-start-marker"))
            > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Log_mutation_appends_to_the_session_log()
    {
        Scaffold(HomeScaffolder.HookShell.Auto, out var home);
        var log = Path.Combine(home, ".wireify", "mutations.log");

        Assert.Equal(0, RunHook(home, "log-mutation").ExitCode);
        Assert.Equal(0, RunHook(home, "log-mutation").ExitCode);

        Assert.True(File.Exists(log));
        Assert.Equal(2, File.ReadAllLines(log).Length);
    }
}
