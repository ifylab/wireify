// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using WireifyCore.Bridge;
using WireifyCore.Mcp;

namespace WireifyCore.Tests;

public class WireifyToolsTests
{
    [Fact]
    public void Read_tool_delegates_with_all_args()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        tools.ReadInputData(FakeBridge.SomeId, "x", 2, 3);

        Assert.Contains($"ReadInputData:{FakeBridge.SomeId}:x:2:3", fake.Calls);
    }

    [Fact]
    public void SetSource_validates_empty_source_before_touching_bridge()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        Assert.Throws<ArgumentException>(() => tools.SetSource(FakeBridge.SomeId, "", PythonRuntime.CPython3));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void SetSource_solves_and_returns_the_report_by_default()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        var result = tools.SetSource(FakeBridge.SomeId, "a = 1");

        Assert.True(result.Solved);
        Assert.Same(FakeBridge.CannedReport, result.Report);
        Assert.Contains($"SetSource:{FakeBridge.SomeId}:a = 1:CPython3:True", fake.Calls);
    }

    [Fact]
    public void SetSource_solve_false_skips_the_report()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        var result = tools.SetSource(FakeBridge.SomeId, "a = 1", solve: false);

        Assert.False(result.Solved);
        Assert.Null(result.Report);
    }

    [Fact]
    public void Run_and_convert_carry_the_post_solve_report()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        Assert.Same(FakeBridge.CannedReport, tools.Run(FakeBridge.SomeId).Report);
        Assert.Same(FakeBridge.CannedReport, tools.ConvertStaged(
            FakeBridge.SomeId, "a = 1", new[] { new IoParamSpec("a") }).Report);
    }

    [Fact]
    public void Summary_staged_data_is_off_by_default_and_inlined_on_request()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        Assert.Null(tools.GetDocumentSummary().Wireify![0].StagedData);

        var staged = tools.GetDocumentSummary(includeStagedData: true).Wireify![0].StagedData;
        Assert.NotNull(staged);
        Assert.Equal("in1", staged![0].Param);
    }

    [Fact]
    public void Create_defaults_to_cpython3()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        tools.CreatePythonComponent();

        Assert.Contains("CreatePythonComponent:CPython3", fake.Calls);
    }

    [Fact]
    public void Registry_builds_every_tool_against_the_sdk()
    {
        // Exercises every McpServerTool.Create delegate cast + options against the real SDK:
        // a wrong signature or bad option would throw here.
        var collection = WireifyToolRegistry.Build(new WireifyTools(new FakeBridge()));

        Assert.Equal(14, collection.Count());
    }

    [Fact]
    public void ConvertStaged_validates_args_and_signals_activity_around_the_call()
    {
        var fake = new FakeBridge();
        var activity = new List<(Guid Id, bool Active)>();
        var tools = new WireifyTools(fake, (id, active) => activity.Add((id, active)));
        var outputs = new[] { new IoParamSpec("points", "list") };

        Assert.Throws<ArgumentException>(() => tools.ConvertStaged(FakeBridge.SomeId, "", outputs));
        Assert.Throws<ArgumentException>(() => tools.ConvertStaged(FakeBridge.SomeId, "a = 1", Array.Empty<IoParamSpec>()));
        Assert.Empty(activity); // rejected input never blips the socket's Working state

        tools.ConvertStaged(FakeBridge.SomeId, "a = 1", outputs, PythonRuntime.CPython3, "demo",
            new[] { new IoParamSpec("in1", "tree") });

        Assert.Contains($"ConvertStaged:{FakeBridge.SomeId}:a = 1:points/list:CPython3:demo:in1/tree", fake.Calls);
        Assert.Equal(new[] { (FakeBridge.SomeId, true), (FakeBridge.SomeId, false) }, activity);
    }

    [Fact]
    public void SetIo_delegates_with_specs()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        tools.SetIo(FakeBridge.SomeId,
            new[] { new IoParamSpec("values", "list") },
            new[] { new IoParamSpec("result") });

        Assert.Contains($"SetIo:{FakeBridge.SomeId}:1:1", fake.Calls);
    }
}
