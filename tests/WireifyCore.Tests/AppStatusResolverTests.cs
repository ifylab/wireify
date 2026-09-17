// SPDX-License-Identifier: Apache-2.0
using System.IO;
using WireifyContract;
using WireifyCore.Connect;
using WireifyCore.Hosting;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>The socket's app facts come from the file system alone: a home is a pure function
/// of the definition's path, so no session is needed to know whether a page exists.</summary>
public class AppStatusResolverTests
{
    static (WireifyPaths Paths, string Root) Temp()
    {
        var root = Path.Combine(Path.GetTempPath(), "wireify-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        return (new WireifyPaths(root, Path.Combine(root, "claude.json")), root);
    }

    [Fact]
    public void An_unsaved_definition_has_no_home()
    {
        var (paths, _) = Temp();
        var s = AppStatusResolver.Compute(paths, null, 9473, true);
        Assert.False(s.HomeExists);
        Assert.False(s.PageExists);
        Assert.Equal("save the definition first", s.Reason);
        Assert.Equal("", s.Address);
    }

    [Fact]
    public void A_home_without_a_page_says_so_and_still_names_its_address()
    {
        var (paths, root) = Temp();
        var gh = Path.Combine(root, "truss.gh");
        File.WriteAllText(gh, "");
        Directory.CreateDirectory(paths.HomeFor(gh));

        var s = AppStatusResolver.Compute(paths, gh, 9473, true);

        Assert.True(s.HomeExists);
        Assert.False(s.PageExists);
        Assert.Equal(WireifyPaths.HomeId(gh), s.HomeId);
        Assert.Equal($"127.0.0.1:9473/app/{s.HomeId}", s.Address);
        Assert.Equal(AppStatusResolver.NoPageReason, s.Reason);
        Assert.Equal(Path.Combine(paths.HomeFor(gh), "app"), s.AppFolder);
    }

    [Fact]
    public void A_page_flips_the_status_and_the_address_needs_a_listening_server()
    {
        var (paths, root) = Temp();
        var gh = Path.Combine(root, "truss.gh");
        var app = Path.Combine(paths.HomeFor(gh), "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "index.html"), "<p>hi</p>");

        var s = AppStatusResolver.Compute(paths, gh, 9473, true);
        Assert.True(s.PageExists);
        Assert.Equal("", s.Reason);

        var down = AppStatusResolver.Compute(paths, gh, 0, false);
        Assert.True(down.PageExists);
        Assert.Equal("", down.Address);
    }
}
