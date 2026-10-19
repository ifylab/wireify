// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WireifyCore.Tests;

/// <summary>The vendored scripting cookbook ships as a thin router plus one reference file per
/// section, so a which-API question costs one targeted read instead of the whole set. These pin
/// the layout: the router stays small, every index entry resolves, and no section body is left
/// in the router.</summary>
public class ScriptingReferenceLayoutTests
{
    const int RouterByteCap = 8 * 1024;

    static string SkillDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "home-template", "skills", "scripting-reference");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("home-template/skills/scripting-reference not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_router_is_small_and_keeps_its_frontmatter()
    {
        var router = Path.Combine(SkillDir(), "SKILL.md");
        var text = File.ReadAllText(router);

        Assert.True(new FileInfo(router).Length <= RouterByteCap, $"router is {new FileInfo(router).Length} bytes; cap {RouterByteCap}");
        Assert.StartsWith("---\nname: scripting-reference\n", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain("\n## 2.", text);
        Assert.Contains("## Reference files", text);
    }

    [Fact]
    public void Every_index_entry_resolves_to_a_reference_file_whose_heading_matches()
    {
        var dir = SkillDir();
        var text = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
        var entries = Regex.Matches(text, @"^- \[(?<title>[^\]]+)\]\(references/(?<file>[a-z0-9-]+\.md)\)", RegexOptions.Multiline)
            .Select(m => (Title: m.Groups["title"].Value, File: m.Groups["file"].Value)).ToList();

        Assert.Equal(8, entries.Count);
        foreach (var (title, file) in entries)
        {
            var path = Path.Combine(dir, "references", file);
            Assert.True(File.Exists(path), $"{file} is indexed but missing");
            var firstLine = File.ReadLines(path).First();
            Assert.Equal("## " + title, firstLine);
        }

        var onDisk = Directory.GetFiles(Path.Combine(dir, "references"), "*.md").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(entries.Select(e => e.File).OrderBy(n => n, StringComparer.Ordinal), onDisk);
    }
}
