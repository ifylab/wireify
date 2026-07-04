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
        public ITerminalHandle? Launch(string homeDir, string? model = null, string? effort = null)
        { Launched = homeDir; Model = model; Effort = effort; return null; }
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
