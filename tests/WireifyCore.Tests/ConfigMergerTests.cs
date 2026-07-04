// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Text.Json.Nodes;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class ConfigMergerTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-cfg-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Creates_mcp_json_when_missing()
    {
        var path = Path.Combine(TempDir(), ".mcp.json");

        ConfigMerger.MergeProjectMcpJson(path, 52801, "sek");

        var entry = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["wireify"]!.AsObject();
        Assert.Equal("http", (string)entry["type"]!);
        Assert.Equal("http://127.0.0.1:52801/mcp", (string)entry["url"]!);
        Assert.Equal("sek", (string)entry["headers"]!["X-Wireify-Secret"]!);
    }

    [Fact]
    public void Preserves_other_servers_and_keys()
    {
        var path = Path.Combine(TempDir(), ".mcp.json");
        File.WriteAllText(path, """{"$schema":"x","mcpServers":{"other":{"type":"stdio","command":"foo"}}}""");

        ConfigMerger.MergeProjectMcpJson(path, 1234, "s");

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("x", (string)root["$schema"]!);
        var servers = root["mcpServers"]!.AsObject();
        Assert.Equal("foo", (string)servers["other"]!["command"]!);
        Assert.True(servers.ContainsKey("wireify"));
    }

    [Fact]
    public void Replaces_existing_wireify_entry_only()
    {
        var path = Path.Combine(TempDir(), ".mcp.json");
        ConfigMerger.MergeProjectMcpJson(path, 1111, "old");
        ConfigMerger.MergeProjectMcpJson(path, 2222, "new");

        var entry = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["wireify"]!.AsObject();
        Assert.Equal("http://127.0.0.1:2222/mcp", (string)entry["url"]!);
        Assert.Equal("new", (string)entry["headers"]!["X-Wireify-Secret"]!);
    }

    [Fact]
    public void Throws_and_leaves_file_intact_on_malformed_json()
    {
        var path = Path.Combine(TempDir(), ".mcp.json");
        const string junk = "{ not valid json ";
        File.WriteAllText(path, junk);

        Assert.Throws<InvalidDataException>(() => ConfigMerger.MergeProjectMcpJson(path, 1, "s"));
        Assert.Equal(junk, File.ReadAllText(path));
    }

    [Fact]
    public void Throws_when_mcpServers_is_not_an_object()
    {
        var path = Path.Combine(TempDir(), ".mcp.json");
        File.WriteAllText(path, """{"mcpServers":"oops"}""");

        Assert.Throws<InvalidDataException>(() => ConfigMerger.MergeProjectMcpJson(path, 1, "s"));
    }

    [Fact]
    public void Claude_json_nests_under_project_scope_and_preserves_rest()
    {
        var path = Path.Combine(TempDir(), ".claude.json");
        File.WriteAllText(path, """{"numStartups":3,"mcpServers":{"global":{"type":"stdio"}}}""");

        ConfigMerger.MergeClaudeJson(path, "/abs/project", 9000, "s");

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(3, (int)root["numStartups"]!);
        Assert.True(root["mcpServers"]!.AsObject().ContainsKey("global"));
        var entry = root["projects"]!["/abs/project"]!["mcpServers"]!["wireify"]!.AsObject();
        Assert.Equal("http://127.0.0.1:9000/mcp", (string)entry["url"]!);
    }
}
