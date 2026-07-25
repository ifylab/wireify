// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WireifyCore.Connect;
using WireifyCore.Mcp;

namespace WireifyCore.Tests;

public class WireifyConnectorTests
{
    sealed class RecordingLauncher : ITerminalLauncher
    {
        public string? Launched;
        public string? Model;
        public string? Effort;
        public string? Title;
        public ITerminalHandle? Launch(string homeDir, string? model = null, string? effort = null, string? title = null)
        { Launched = homeDir; Model = model; Effort = effort; Title = title; return null; }
    }

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

    static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-connect-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Connect_scaffolds_home_writes_config_trusts_and_launches()
    {
        var root = TempRoot();
        var paths = new WireifyPaths(root, Path.Combine(root, "claude.json"));
        var launcher = new RecordingLauncher();
        var connector = new WireifyConnector(paths, new HomeScaffolder(TemplateRoot()), launcher);

        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), "sekret");
        host.Start(54000);

        var result = connector.Connect("/projects/tower.gh", host);

        Assert.Equal(host.Port, result.Port);
        Assert.Equal("sekret", result.Secret);
        Assert.True(File.Exists(Path.Combine(result.HomeDir, "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(result.HomeDir, ".claude", "skills", "wireify-loop", "SKILL.md")));
        Assert.True(File.Exists(paths.SharedDefaults));
        Assert.Equal(result.HomeDir, launcher.Launched);
        Assert.Equal("sonnet", launcher.Model);  // the home's seeded wireify.json drives the spawn flags
        Assert.Equal("high", launcher.Effort);

        var entry = JsonNode.Parse(File.ReadAllText(result.McpConfigPath))!["mcpServers"]!["wireify"]!.AsObject();
        Assert.Equal($"http://127.0.0.1:{host.Port}/mcp", (string)entry["url"]!);
        Assert.Equal("sekret", (string)entry["headers"]!["X-Wireify-Secret"]!);
        // The session header the server routes documents by, and the window title that makes two
        // definitions' terminals tellable apart.
        Assert.Equal(Path.GetFileName(result.HomeDir), (string)entry["headers"]!["X-Wireify-Home"]!);
        Assert.Equal("Wireify - tower.gh", launcher.Title);

        // The home is pre-trusted so the scaffolded allowlist applies from the first session.
        var trustKey = Path.GetFullPath(result.HomeDir).Replace('\\', '/');
        var claude = JsonNode.Parse(File.ReadAllText(paths.ClaudeJson))!.AsObject();
        Assert.True((bool)claude["projects"]![trustKey]!["hasTrustDialogAccepted"]!);
    }

    [Fact]
    public void Home_model_read_honors_user_edits_absence_and_unsafe_values()
    {
        var home = TempRoot();
        var wireifyJson = Path.Combine(home, "wireify.json");

        Assert.Null(WireifyConnector.ReadHomeModel(home)); // no file -> no flag

        File.WriteAllText(wireifyJson, """{ "model": "opus" }""");
        Assert.Equal("opus", WireifyConnector.ReadHomeModel(home));

        File.WriteAllText(wireifyJson, "{}"); // key deleted -> user's own default governs
        Assert.Null(WireifyConnector.ReadHomeModel(home));

        File.WriteAllText(wireifyJson, """{ "model": "sonnet; rm -rf /" }"""); // never shelled out
        Assert.Null(WireifyConnector.ReadHomeModel(home));

        File.WriteAllText(wireifyJson, """{ "effort": "high" }""");
        Assert.Equal("high", WireifyConnector.ReadHomeEffort(home));

        File.WriteAllText(wireifyJson, """{ "effort": "ultra-mega" }"""); // not a known level
        Assert.Null(WireifyConnector.ReadHomeEffort(home));

        File.WriteAllText(wireifyJson, """{ "model": "default", "effort": "Default" }"""); // released
        Assert.Null(WireifyConnector.ReadHomeModel(home));
        Assert.Null(WireifyConnector.ReadHomeEffort(home));
    }

    [Fact]
    public void Claude_command_builds_flags_only_for_safe_values()
    {
        Assert.Equal("claude --model sonnet", SystemTerminalLauncher.ClaudeCommand("sonnet"));
        Assert.Equal("claude --model sonnet[1m]", SystemTerminalLauncher.ClaudeCommand("sonnet[1m]"));
        Assert.Equal("claude --model sonnet --effort high", SystemTerminalLauncher.ClaudeCommand("sonnet", "high"));
        Assert.Equal("claude --effort medium", SystemTerminalLauncher.ClaudeCommand(null, "medium"));
        Assert.Equal("claude", SystemTerminalLauncher.ClaudeCommand(null));
        Assert.Equal("claude", SystemTerminalLauncher.ClaudeCommand("bad value && calc", "sudo rm"));
    }

    [Fact]
    public void Connect_records_identity_then_adopts_the_home_when_the_file_moves()
    {
        var root = TempRoot();
        var paths = new WireifyPaths(root, Path.Combine(root, "claude.json"));
        var connector = new WireifyConnector(paths, new HomeScaffolder(TemplateRoot()), new RecordingLauncher());

        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), "sekret");
        host.Start(54200);

        // First Connect on a real .gh: identity recorded.
        var ghDir = Path.Combine(root, "files");
        Directory.CreateDirectory(ghDir);
        var oldGh = Path.Combine(ghDir, "tower.gh");
        File.WriteAllText(oldGh, "definition-bytes");
        var first = connector.Connect(oldGh, host);
        var record = HomeIdentity.Read(first.HomeDir);
        Assert.NotNull(record);
        Assert.Equal(Path.GetFullPath(oldGh), record!.GhPath);
        Assert.Equal(64, record.GhSha256!.Length);

        // The definition accumulates a lesson, then the user moves the file.
        File.AppendAllText(Path.Combine(first.HomeDir, "MEMORY.md"), "\n### 2026-07-09 [W1] the-marker-lesson\n");
        var newDir = Path.Combine(root, "moved");
        Directory.CreateDirectory(newDir);
        var newGh = Path.Combine(newDir, "tower.gh");
        File.Move(oldGh, newGh);

        // Second Connect: the old home is adopted (COPIED onto the new id), memory intact. The
        // original stays — a live terminal may occupy it (the Windows lock that killed the move
        // design, round 18C) — marked so it never matches again, aging via the sweep.
        var second = connector.Connect(newGh, host);

        Assert.NotEqual(first.HomeDir, second.HomeDir);
        Assert.Contains(second.Steps, s => s.Message.StartsWith("adopted memory from") && s.Ok);
        Assert.Contains("the-marker-lesson", File.ReadAllText(Path.Combine(second.HomeDir, "MEMORY.md")));
        Assert.Equal(Path.GetFullPath(newGh), HomeIdentity.Read(second.HomeDir)!.GhPath); // re-keyed to the new path
        Assert.True(Directory.Exists(first.HomeDir)); // copy, never move
        var oldRecord = HomeIdentity.Read(first.HomeDir);
        Assert.Equal(Path.GetFileName(second.HomeDir), oldRecord!.AdoptedInto);
        Assert.NotNull(oldRecord.OrphanedAtUtc); // archive clock started at adoption
        // A clean adoption leaves no handoff file behind.
        Assert.False(File.Exists(Path.Combine(second.HomeDir, ".wireify", "adoption-candidates.json")));
    }

    [Fact]
    public void Connect_reports_the_memory_glance_and_regenerates_the_homes_index()
    {
        var root = TempRoot();
        var paths = new WireifyPaths(root, Path.Combine(root, "claude.json"));
        var connector = new WireifyConnector(paths, new HomeScaffolder(TemplateRoot()), new RecordingLauncher());

        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), "sekret");
        host.Start(54300);

        var ghDir = Path.Combine(root, "files");
        Directory.CreateDirectory(ghDir);
        var gh = Path.Combine(ghDir, "tower.gh");
        File.WriteAllText(gh, "definition-bytes");

        // Fresh home: the glance says so plainly.
        var first = connector.Connect(gh, host);
        Assert.Contains(first.Steps, s => s.Message == "memory: no lessons yet" && s.Kind == "home" && s.Ok);

        // A lesson lands; the next Connect counts and dates it, with the header's usage numbers.
        File.AppendAllText(Path.Combine(first.HomeDir, "MEMORY.md"),
            "### 2026-07-09 [W1] marker\nSymptom: s\nCause: c\nFix: f\nApplies-when: a\n");
        var second = connector.Connect(gh, host);
        Assert.Contains(second.Steps, s =>
            s.Message.StartsWith("memory: 1 lesson (last 2026-07-09), ") && s.Message.EndsWith("/8,000 chars"));

        // And the homes index exists at the root, naming this home as active.
        var index = File.ReadAllText(Path.Combine(root, "homes.md"));
        Assert.Contains(Path.GetFileName(first.HomeDir), index);
        Assert.Contains("| active |", index);
    }

    [Fact]
    public async Task Connect_config_addresses_the_live_server()
    {
        // End to end: the .mcp.json the user's Claude will read must actually reach a working server.
        var root = TempRoot();
        var paths = new WireifyPaths(root, Path.Combine(root, "claude.json"));
        var connector = new WireifyConnector(paths, new HomeScaffolder(TemplateRoot()), new NullTerminalLauncher());

        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), "live-secret");
        host.Start(54100);

        var result = connector.Connect("/projects/widget.gh", host);
        var entry = JsonNode.Parse(File.ReadAllText(result.McpConfigPath))!["mcpServers"]!["wireify"]!.AsObject();
        var url = (string)entry["url"]!;
        var secret = (string)entry["headers"]!["X-Wireify-Secret"]!;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", secret);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("get_document_summary", body);
    }
}
