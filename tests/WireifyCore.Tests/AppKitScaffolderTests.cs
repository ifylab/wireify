// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using WireifyCore.Connect;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>The kit-stamping ownership contract: kit/ is Wireify's and is overwritten; the
/// agent/user's page, manifest, and theme are seeded only when absent and never touched after;
/// the Connect-time refresh reaches only homes that already have an app.</summary>
public class AppKitScaffolderTests : IDisposable
{
    readonly string _root;
    readonly string _template;
    readonly string _home;

    public AppKitScaffolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wireify-kit-" + Guid.NewGuid().ToString("N"));
        _template = Path.Combine(_root, "home-template");
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(_template, "app", "kit", "fonts"));
        File.WriteAllText(Path.Combine(_template, "app", "kit", "ify-app.css"), "/* kit v1 */");
        File.WriteAllText(Path.Combine(_template, "app", "kit", "fonts", "a.woff2"), "font");
        File.WriteAllText(Path.Combine(_template, "app", "starter.html"), "<!doctype html><title>starter</title>");
        File.WriteAllText(Path.Combine(_template, "app", "report.html"), "<!doctype html><title>report shape</title>");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    [Fact]
    public void Scaffold_creates_app_kit_page_and_manifest()
    {
        var report = AppKitScaffolder.Scaffold(_template, _home);

        Assert.Equal("/* kit v1 */", File.ReadAllText(Path.Combine(_home, "app", "kit", "ify-app.css")));
        Assert.True(File.Exists(Path.Combine(_home, "app", "kit", "fonts", "a.woff2")));
        Assert.Contains("starter", File.ReadAllText(Path.Combine(_home, "app", "index.html")));
        Assert.Contains("\"controls\": []", File.ReadAllText(Path.Combine(_home, "app", "manifest.json")));
        // The receipt reports ACTION: a fresh home seeds all three agent/user files (S5.4f).
        Assert.Equal(new[] { "index.html", "manifest.json", "theme.css" }, report.Seeded);
        Assert.Empty(report.Skipped);
        Assert.Equal("panel", report.Template);
    }

    [Fact]
    public void Scaffold_rerun_reports_the_existing_files_as_skipped()
    {
        AppKitScaffolder.Scaffold(_template, _home);

        var rerun = AppKitScaffolder.Scaffold(_template, _home, "report");

        // Nothing seeded the second time — and the receipt SAYS so, which is what lets the
        // calling agent tell "I just created your page" from "your page was already here".
        Assert.Empty(rerun.Seeded);
        Assert.Equal(new[] { "index.html", "manifest.json", "theme.css" }, rerun.Skipped);
        Assert.Equal("report", rerun.Template);
        Assert.Contains("starter", File.ReadAllText(Path.Combine(_home, "app", "index.html")));
    }

    [Fact]
    public void Scaffold_rerun_refreshes_kit_but_never_touches_agent_files()
    {
        AppKitScaffolder.Scaffold(_template, _home);
        File.WriteAllText(Path.Combine(_home, "app", "index.html"), "<!doctype html><title>mine</title>");
        File.WriteAllText(Path.Combine(_home, "app", "manifest.json"), "{\"controls\":[{\"id\":\"x\"}]}");
        File.WriteAllText(Path.Combine(_home, "app", "theme.css"), ":root { --ify-accent: red; }");
        File.WriteAllText(Path.Combine(_template, "app", "kit", "ify-app.css"), "/* kit v2 */");

        AppKitScaffolder.Scaffold(_template, _home);

        Assert.Equal("/* kit v2 */", File.ReadAllText(Path.Combine(_home, "app", "kit", "ify-app.css")));
        Assert.Contains("mine", File.ReadAllText(Path.Combine(_home, "app", "index.html")));
        Assert.Contains("\"id\":\"x\"", File.ReadAllText(Path.Combine(_home, "app", "manifest.json")));
        Assert.Contains("red", File.ReadAllText(Path.Combine(_home, "app", "theme.css")));
    }

    [Fact]
    public void Scaffold_report_template_seeds_the_report_shape()
    {
        AppKitScaffolder.Scaffold(_template, _home, template: "report");

        Assert.Contains("report shape", File.ReadAllText(Path.Combine(_home, "app", "index.html")));
    }

    [Fact]
    public void Scaffold_template_never_replaces_an_existing_page()
    {
        AppKitScaffolder.Scaffold(_template, _home); // seeds the starter

        AppKitScaffolder.Scaffold(_template, _home, template: "report");

        Assert.Contains("starter", File.ReadAllText(Path.Combine(_home, "app", "index.html")));
    }

    [Fact]
    public void RefreshKit_skips_homes_without_an_app()
    {
        AppKitScaffolder.RefreshKit(_template, _home);

        Assert.False(Directory.Exists(Path.Combine(_home, "app")));
    }

    [Fact]
    public void RefreshKit_restamps_kit_only()
    {
        AppKitScaffolder.Scaffold(_template, _home);
        File.WriteAllText(Path.Combine(_home, "app", "index.html"), "<!doctype html><title>mine</title>");
        File.WriteAllText(Path.Combine(_template, "app", "kit", "ify-app.css"), "/* kit v3 */");

        AppKitScaffolder.RefreshKit(_template, _home);

        Assert.Equal("/* kit v3 */", File.ReadAllText(Path.Combine(_home, "app", "kit", "ify-app.css")));
        Assert.Contains("mine", File.ReadAllText(Path.Combine(_home, "app", "index.html")));
    }
}
