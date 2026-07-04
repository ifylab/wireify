// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class HomeScaffolderTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-home-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Walk up from the test bin dir to the repo's home-template/ (wireify/home-template).
    static string TemplateRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "home-template");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("home-template not found walking up from " + AppContext.BaseDirectory);
    }

    static HomeScaffolder New() => new(TemplateRoot());

    [Fact]
    public void Fresh_scaffold_writes_core_files_with_substitution()
    {
        var home = TempDir();

        New().ScaffoldHome(home, new HomeScaffolder.Substitutions(52801, "sek", "tower.gh"));

        var claude = File.ReadAllText(Path.Combine(home, "CLAUDE.md"));
        Assert.Contains("tower.gh", claude);
        Assert.DoesNotContain("{{GH_FILE}}", claude);
        Assert.True(File.Exists(Path.Combine(home, ".claude", "settings.json")));
        Assert.True(File.Exists(Path.Combine(home, "MEMORY.md")));
        Assert.True(File.Exists(Path.Combine(home, ".claude", "skills", "wireify-loop", "SKILL.md")));
    }

    [Fact]
    public void Reconnect_preserves_memory_and_refreshes_only_the_claude_md_managed_block()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var memory = Path.Combine(home, "MEMORY.md");
        var claude = Path.Combine(home, "CLAUDE.md");
        File.WriteAllText(memory, "LESSON: keep this");
        File.AppendAllText(claude, "\nuser note below the block\n");

        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(2, "b", "other.gh"));

        Assert.Equal("LESSON: keep this", File.ReadAllText(memory));
        var text = File.ReadAllText(claude);
        Assert.Contains("other.gh", text);                     // block refreshed with new subs
        Assert.Contains("user note below the block", text);    // user content outside it untouched
        Assert.DoesNotContain("f.gh", text.Replace("other.gh", "")); // old block gone, not duplicated
    }

    [Fact]
    public void Legacy_claude_md_without_markers_keeps_content_and_gains_the_block()
    {
        var home = TempDir();
        var claude = Path.Combine(home, "CLAUDE.md");
        Directory.CreateDirectory(home);
        File.WriteAllText(claude, "# old render the user may have edited\n");

        New().ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var text = File.ReadAllText(claude);
        Assert.StartsWith("# old render the user may have edited", text);
        Assert.Contains("<!-- wireify:begin", text);
        Assert.Contains("@MEMORY.md", text); // the import now reaches legacy homes too
    }

    [Fact]
    public void Reconnect_refreshes_settings_and_skills()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var settings = Path.Combine(home, ".claude", "settings.json");
        File.WriteAllText(settings, "{}");

        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        Assert.Contains("mcp__wireify__", File.ReadAllText(settings));
    }

    [Fact]
    public void Wireify_json_seeds_fresh_then_merges_per_key_preserving_user_values()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var wireifyJson = Path.Combine(home, "wireify.json");
        var seeded = File.ReadAllText(wireifyJson);
        Assert.Contains("\"model\": \"sonnet\"", seeded);
        Assert.Contains("\"effort\": \"high\"", seeded);

        // A key the user edited survives; a key their older file lacks is added on re-Connect.
        File.WriteAllText(wireifyJson, "{ \"model\": \"opus\" }");
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(2, "b", "f.gh"));

        Assert.Equal("opus", WireifyConnector.ReadHomeModel(home));
        Assert.Equal("high", WireifyConnector.ReadHomeEffort(home));

        // Malformed file: left alone, never destroyed.
        File.WriteAllText(wireifyJson, "{ not json");
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(3, "c", "f.gh"));
        Assert.Equal("{ not json", File.ReadAllText(wireifyJson));
    }

    [Fact]
    public void Seed_defaults_writes_once_then_preserves_edits()
    {
        var path = Path.Combine(TempDir(), "shared", "defaults.md");
        var s = New();

        s.SeedSharedDefaults(path);
        Assert.True(File.Exists(path));

        File.WriteAllText(path, "user-edited defaults");
        s.SeedSharedDefaults(path);
        Assert.Equal("user-edited defaults", File.ReadAllText(path));
    }
}
