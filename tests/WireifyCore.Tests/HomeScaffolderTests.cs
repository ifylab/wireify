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
    public void Fresh_scaffold_ships_dev_mode_skill_and_devlog_permissions()
    {
        var home = TempDir();

        New().ScaffoldHome(home, new HomeScaffolder.Substitutions(52801, "sek", "tower.gh"));

        Assert.True(File.Exists(Path.Combine(home, ".claude", "skills", "wireify-dev", "SKILL.md")));
        var settings = File.ReadAllText(Path.Combine(home, ".claude", "settings.json"));
        Assert.Contains("Write(~/.ify/wireify/devlog.md)", settings);
    }

    [Fact]
    public void Reconnect_preserves_lessons_and_refreshes_only_the_managed_blocks()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var memory = Path.Combine(home, "MEMORY.md");
        var claude = Path.Combine(home, "CLAUDE.md");
        // A legacy-style ledger the user (or an earlier build) wrote without markers: the block
        // is PREPENDED above it and every byte survives below.
        File.WriteAllText(memory, "LESSON: keep this");
        File.AppendAllText(claude, "\nuser note below the block\n");

        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(2, "b", "other.gh"));

        var mem = File.ReadAllText(memory);
        Assert.StartsWith("<!-- wireify:begin", mem);
        Assert.EndsWith("LESSON: keep this", mem);
        Assert.NotEmpty(Directory.GetFiles(home, "MEMORY.md.bak.*")); // legacy transform snapshots first

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

    [Fact]
    public void Fresh_scaffold_leaves_no_temp_or_bak_files_and_renders_all_tokens()
    {
        var home = TempDir();

        var result = New().ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        Assert.Null(result.MemoryNote);
        Assert.Empty(Directory.GetFiles(home, "*.tmp-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(home, "*.bak.*", SearchOption.AllDirectories));

        var mem = File.ReadAllText(Path.Combine(home, "MEMORY.md"));
        Assert.Contains("/ 8,000 chars", mem);       // real usage line, not the provisional seed
        Assert.DoesNotContain("{{MEM_", mem);
        var claude = File.ReadAllText(Path.Combine(home, "CLAUDE.md"));
        Assert.DoesNotContain("{{MEM_", claude);
        Assert.DoesNotContain("over budget", claude); // directive absent under budget
    }

    [Fact]
    public void Memory_header_refreshes_while_appended_lessons_survive_byte_for_byte()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var memory = Path.Combine(home, "MEMORY.md");
        var lesson = "### 2026-07-06 [W1] wrong doc lookup\nSymptom: s\nCause: c\nFix: f\nApplies-when: a\n";
        File.AppendAllText(memory, lesson);

        var result = s.ScaffoldHome(home, new HomeScaffolder.Substitutions(2, "b", "renamed.gh"));

        var mem = File.ReadAllText(memory);
        Assert.Null(result.MemoryNote);              // conforming, nothing to maintain
        Assert.Contains("renamed.gh", mem);          // header refreshed
        Assert.EndsWith(lesson, mem);                // lesson bytes untouched
        Assert.Equal(1, CountOf(mem, "<!-- wireify:begin")); // block replaced, not duplicated
    }

    [Fact]
    public void Ledger_overflow_archives_oldest_entries_snapshot_first()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        var memory = Path.Combine(home, "MEMORY.md");
        for (var i = 9; i >= 1; i--) // append below = older; newest (9) stays on top
            File.AppendAllText(memory,
                $"### 2026-07-0{Math.Min(i, 6)} [-] lesson {i}\nSymptom: {new string('x', 1200)}\nFix: f\n");

        var result = s.ScaffoldHome(home, new HomeScaffolder.Substitutions(2, "b", "f.gh"));

        Assert.NotNull(result.MemoryNote);
        Assert.Contains("archived", result.MemoryNote);
        Assert.True(File.ReadAllText(memory).Length <= HomeScaffolder.MemoryBudgetChars);
        Assert.Contains("lesson 9", File.ReadAllText(memory));          // newest kept
        var archive = File.ReadAllText(Path.Combine(home, "MEMORY-archive.md"));
        Assert.Contains("lesson 1", archive);                            // oldest moved out
        Assert.NotEmpty(Directory.GetFiles(home, "MEMORY.md.bak.*"));    // snapshot preceded the mutation
    }

    [Fact]
    public void Bak_retention_keeps_the_newest_three()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));
        var memory = Path.Combine(home, "MEMORY.md");

        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 8; i++)
                File.AppendAllText(memory,
                    $"### 2026-07-06 [-] r{round} lesson {i}\nSymptom: {new string('x', 1200)}\nFix: f\n");
            s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));
        }

        Assert.True(Directory.GetFiles(home, "MEMORY.md.bak.*").Length <= 3);
    }

    [Fact]
    public void Directive_renders_when_the_ledger_stays_over_budget()
    {
        var home = TempDir();
        var s = New();
        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        // One oversized entry: conforming, but maintenance never archives the last survivor —
        // the file stays over budget and the CLAUDE.md directive must fire.
        var memory = Path.Combine(home, "MEMORY.md");
        File.AppendAllText(memory, $"### 2026-07-06 [-] huge\nSymptom: {new string('x', 9000)}\nFix: f\n");

        s.ScaffoldHome(home, new HomeScaffolder.Substitutions(2, "b", "f.gh"));

        var claude = File.ReadAllText(Path.Combine(home, "CLAUDE.md"));
        Assert.Contains("over budget", claude);
        Assert.Contains("consolidate MEMORY.md", claude);
    }

    [Fact]
    public void Unmanaged_ledger_is_reported_and_left_alone()
    {
        var home = TempDir();
        Directory.CreateDirectory(home);
        var memory = Path.Combine(home, "MEMORY.md");
        File.WriteAllText(memory, "free text lessons from 0.1\nno structure at all\n");

        var result = New().ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        Assert.NotNull(result.MemoryNote);
        Assert.Contains("unmanaged", result.MemoryNote);
        Assert.EndsWith("free text lessons from 0.1\nno structure at all\n", File.ReadAllText(memory));
    }

    [Fact]
    public void Claude_md_imports_the_shared_defaults()
    {
        var home = TempDir();

        New().ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"));

        Assert.Contains("@~/.ify/wireify/defaults.md", File.ReadAllText(Path.Combine(home, "CLAUDE.md")));
    }

    [Fact]
    public void Defaults_merge_appends_missing_seed_sections_preserving_user_content()
    {
        var path = Path.Combine(TempDir(), "defaults.md");
        var s = New();
        // An older install: one section, user-edited; everything newer is missing.
        File.WriteAllText(path, "# Wireify defaults (shared)\n\nmy intro\n\n## Units and tolerance\n\n- my custom units rule\n");

        s.MergeSharedDefaults(path);

        var text = File.ReadAllText(path);
        Assert.Contains("- my custom units rule", text);              // user bytes verbatim
        Assert.Contains("## Promoted lessons", text);                 // new seed section arrived
        Assert.Contains("## Code style", text);
        Assert.Equal(1, CountOf(text, "## Units and tolerance"));     // never duplicated
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "defaults.md.bak.*"));
    }

    [Fact]
    public void Defaults_merge_is_idempotent()
    {
        var path = Path.Combine(TempDir(), "defaults.md");
        var s = New();
        File.WriteAllText(path, "## Units and tolerance\n\n- mine\n");

        s.MergeSharedDefaults(path);
        var once = File.ReadAllText(path);
        s.MergeSharedDefaults(path);

        Assert.Equal(once, File.ReadAllText(path));
    }

    [Fact]
    public void Freeform_defaults_without_sections_are_left_alone()
    {
        var path = Path.Combine(TempDir(), "defaults.md");
        var s = New();
        File.WriteAllText(path, "the user rewrote this file entirely, no headings at all\n");

        s.MergeSharedDefaults(path);

        Assert.Equal("the user rewrote this file entirely, no headings at all\n", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "defaults.md.bak.*"));
    }

    [Fact]
    public void Shared_skills_merge_into_the_home_with_bundled_winning_collisions()
    {
        var home = TempDir();
        var shared = TempDir();
        Directory.CreateDirectory(Path.Combine(shared, "my-firm-skill"));
        File.WriteAllText(Path.Combine(shared, "my-firm-skill", "SKILL.md"), "firm procedure");
        Directory.CreateDirectory(Path.Combine(shared, "wireify-loop"));
        File.WriteAllText(Path.Combine(shared, "wireify-loop", "SKILL.md"), "OVERRIDE ATTEMPT");

        New().ScaffoldHome(home, new HomeScaffolder.Substitutions(1, "a", "f.gh"), shared);

        var skills = Path.Combine(home, ".claude", "skills");
        Assert.Equal("firm procedure", File.ReadAllText(Path.Combine(skills, "my-firm-skill", "SKILL.md")));
        var loop = File.ReadAllText(Path.Combine(skills, "wireify-loop", "SKILL.md"));
        Assert.DoesNotContain("OVERRIDE ATTEMPT", loop); // the bundled skill stays canonical
        Assert.Contains("wireify", loop);
    }

    static int CountOf(string text, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
