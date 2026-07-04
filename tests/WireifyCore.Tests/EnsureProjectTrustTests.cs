// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Text.Json.Nodes;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class EnsureProjectTrustTests
{
    static string TempFile() => Path.Combine(
        Path.GetTempPath(), "wf-trust-" + Path.GetRandomFileName(), "claude.json");

    [Fact]
    public void Creates_config_and_seeds_trust_with_forward_slash_key()
    {
        var path = TempFile();
        var home = Path.Combine(Path.GetTempPath(), "wf-home-" + Path.GetRandomFileName());

        ConfigMerger.EnsureProjectTrust(path, home);

        var key = Path.GetFullPath(home).Replace('\\', '/');
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True((bool)root["projects"]![key]!["hasTrustDialogAccepted"]!);
    }

    [Fact]
    public void Preserves_existing_content_and_is_idempotent()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "theme": "dark",
              "projects": {
                "/somewhere/else": { "mcpServers": { "other": { "type": "http", "url": "http://x/" } } }
              }
            }
            """);
        var home = Path.Combine(Path.GetTempPath(), "wf-home-" + Path.GetRandomFileName());

        ConfigMerger.EnsureProjectTrust(path, home);
        ConfigMerger.EnsureProjectTrust(path, home); // idempotent

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("dark", (string)root["theme"]!);
        Assert.Equal("http://x/", (string)root["projects"]!["/somewhere/else"]!["mcpServers"]!["other"]!["url"]!);
        var key = Path.GetFullPath(home).Replace('\\', '/');
        Assert.True((bool)root["projects"]![key]!["hasTrustDialogAccepted"]!);
    }

    [Fact]
    public void Refuses_to_overwrite_malformed_config()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.Throws<InvalidDataException>(() =>
            ConfigMerger.EnsureProjectTrust(path, "/some/home"));
        Assert.Equal("{ not json", File.ReadAllText(path)); // untouched
    }
}
