// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using System.Reflection;
using ModelContextProtocol;
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

        // Validation surfaces as McpException — the one exception type whose message the MCP SDK
        // forwards to the client instead of masking.
        var ex = Assert.Throws<McpException>(() => tools.SetSource(FakeBridge.SomeId, "", PythonRuntime.CPython3));
        Assert.Contains("set_source failed", ex.Message);
        Assert.Contains("source is required", ex.Message);
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
        Assert.Contains($"SetSource:{FakeBridge.SomeId}:a = 1:CPython3:True:False", fake.Calls);
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
    public void SetSource_passes_the_external_edit_override_through()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        tools.SetSource(FakeBridge.SomeId, "a = 1", overwriteExternalEdits: true);

        Assert.Contains($"SetSource:{FakeBridge.SomeId}:a = 1:CPython3:True:True", fake.Calls);
    }

    [Fact]
    public void RuntimeInfo_carries_the_loaded_wireify_build()
    {
        var tools = new WireifyTools(new FakeBridge());

        var info = tools.GetRuntimeInfo();

        Assert.False(string.IsNullOrWhiteSpace(info.WireifyBuild));
        Assert.StartsWith(WireifyCore.WireifyBuild.Version, info.WireifyBuild);
    }

    [Fact]
    public void Build_identity_reports_the_assembly_version_and_a_compile_time_stamp()
    {
        // The csproj pins 0.2.0 and bakes the stamp into the informational version at compile
        // time — file times get rewritten by Unblock-File/copies (live-observed), so only
        // assembly metadata is trustworthy after a zip swap.
        Assert.StartsWith("0.2", WireifyCore.WireifyBuild.Version);
        Assert.Contains(WireifyCore.WireifyBuild.Version, WireifyCore.WireifyBuild.Describe());
        Assert.Contains("build", WireifyCore.WireifyBuild.Describe());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}-\d{4}$", WireifyCore.WireifyBuild.Stamp);
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
    public void Wire_defaults_to_strict_and_returns_the_receipt()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);
        var toId = Guid.NewGuid();

        var result = tools.Wire(FakeBridge.SomeId, 0, toId, 1);

        Assert.Contains($"Wire:{FakeBridge.SomeId}:0:{toId}:1:Strict", fake.Calls);
        Assert.Equal("strict", result.Mode);
        Assert.Equal(toId, result.ToId);
        Assert.Empty(result.ReplacedSources);
    }

    [Fact]
    public void Wire_strict_refusal_surfaces_the_input_wired_protocol()
    {
        var occupant = new WireEndInfo(Guid.NewGuid(), "Entwine", "Result");
        var fake = new FakeBridge { WireOccupiedBy = occupant };
        var tools = new WireifyTools(fake);

        var ex = Assert.Throws<McpException>(() => tools.Wire(FakeBridge.SomeId, 0, FakeBridge.SomeId, 0));

        Assert.Contains("wire failed", ex.Message);
        Assert.Contains("WIREIFY_INPUT_WIRED", ex.Message);
        Assert.Contains("Entwine.Result", ex.Message);
    }

    [Fact]
    public void Wire_replace_mode_passes_through_and_reports_replaced_sources()
    {
        var occupant = new WireEndInfo(Guid.NewGuid(), "Entwine", "Result");
        var fake = new FakeBridge { WireOccupiedBy = occupant };
        var tools = new WireifyTools(fake);
        var toId = Guid.NewGuid();

        var result = tools.Wire(FakeBridge.SomeId, 0, toId, 1, WireMode.Replace);

        Assert.Contains($"Wire:{FakeBridge.SomeId}:0:{toId}:1:Replace", fake.Calls);
        Assert.Equal("replace", result.Mode);
        Assert.Equal(new[] { occupant }, result.ReplacedSources);
    }

    [Fact]
    public void Registry_builds_every_tool_against_the_sdk()
    {
        // Exercises every McpServerTool.Create delegate cast + options against the real SDK:
        // a wrong signature or bad option would throw here.
        var collection = WireifyToolRegistry.Build(new WireifyTools(new FakeBridge()));

        Assert.Equal(16, collection.Count());
    }

    [Fact]
    public void ConvertStaged_validates_args_and_signals_activity_around_the_call()
    {
        var fake = new FakeBridge();
        var activity = new List<(Guid Id, bool Active)>();
        var tools = new WireifyTools(fake, (id, active) => activity.Add((id, active)));
        var outputs = new[] { new IoParamSpec("points", "list") };

        Assert.Throws<McpException>(() => tools.ConvertStaged(FakeBridge.SomeId, "", outputs));
        Assert.Throws<McpException>(() => tools.ConvertStaged(FakeBridge.SomeId, "a = 1", Array.Empty<IoParamSpec>()));
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

    [Fact]
    public void Delete_delegates_and_signals_activity()
    {
        var fake = new FakeBridge();
        var activity = new List<(Guid Id, bool Active)>();
        var tools = new WireifyTools(fake, (id, active) => activity.Add((id, active)));

        var deleted = tools.DeleteComponent(FakeBridge.SomeId);

        Assert.Equal(FakeBridge.SomeId, deleted.Id);
        Assert.Contains($"DeleteComponent:{FakeBridge.SomeId}", fake.Calls);
        Assert.Equal(new[] { (FakeBridge.SomeId, true), (FakeBridge.SomeId, false) }, activity);
    }

    [Fact]
    public void SetPanelText_validates_and_delegates()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        Assert.Throws<McpException>(() => tools.SetPanelText(FakeBridge.SomeId, ""));
        Assert.Empty(fake.Calls);

        var result = tools.SetPanelText(FakeBridge.SomeId, @"C:\data\export.json");
        Assert.Equal(@"C:\data\export.json".Length, result.Length);
        Assert.Contains($"SetPanelText:{FakeBridge.SomeId}:C:\\data\\export.json", fake.Calls);
    }

    [Fact]
    public void Bridge_failures_surface_as_McpException_naming_the_innermost_cause()
    {
        // The SDK masks every other exception type into "An error occurred invoking 'x'." —
        // reflection/task wrappers must be stripped and the real failure forwarded.
        var wrapped = new TargetInvocationException(
            new AggregateException(new InvalidOperationException("engine wedged")));
        var tools = new WireifyTools(new ThrowingBridge(wrapped));

        var ex = Assert.Throws<McpException>(() => tools.SetSource(FakeBridge.SomeId, "a = 1"));

        Assert.Contains("set_source failed", ex.Message);
        Assert.Contains("InvalidOperationException", ex.Message);
        Assert.Contains("engine wedged", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void Read_tool_failures_carry_the_tool_name()
    {
        var tools = new WireifyTools(new ThrowingBridge(new ArgumentException("No input 'in9'.")));

        var ex = Assert.Throws<McpException>(() => tools.ReadInputData(FakeBridge.SomeId, "in9"));

        Assert.Contains("read_input_data failed", ex.Message);
        Assert.Contains("No input 'in9'.", ex.Message);
    }

    [Fact]
    public void McpExceptions_pass_through_unwrapped()
    {
        var original = new McpException("already client-facing");
        var tools = new WireifyTools(new ThrowingBridge(original));

        var ex = Assert.Throws<McpException>(() => tools.GetRuntimeInfo());

        Assert.Same(original, ex);
    }

    [Fact]
    public void GetDocumentSummary_passes_cap_and_filter_to_the_bridge()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        tools.GetDocumentSummary(maxComponents: 50, nameFilter: "panel");

        Assert.Contains("GetDocumentSummary:False:50:panel", fake.Calls);
    }

    [Fact]
    public void Leash_line_appears_from_the_second_consecutive_refusal()
    {
        var fake = new FakeBridge { ConvertStagedRefuses = true };
        var tools = new WireifyTools(fake);
        var outputs = new[] { new IoParamSpec("a") };

        var first = tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs);
        var second = tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs);

        Assert.DoesNotContain("LEASH", first.Error);
        Assert.Contains("LEASH", second.Error);
    }

    [Fact]
    public void Leash_line_appears_from_the_second_consecutive_exception()
    {
        var fake = new FakeBridge { ConvertStagedThrows = new InvalidOperationException("boom") };
        var tools = new WireifyTools(fake);
        var outputs = new[] { new IoParamSpec("a") };

        var first = Assert.Throws<McpException>(() => tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs));
        var second = Assert.Throws<McpException>(() => tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs));

        Assert.DoesNotContain("LEASH", first.Message);
        Assert.Contains("convert_staged failed — InvalidOperationException: boom", second.Message);
        Assert.Contains("LEASH", second.Message);
    }

    [Fact]
    public void Success_resets_the_leash_counter()
    {
        var fake = new FakeBridge { ConvertStagedRefuses = true };
        var tools = new WireifyTools(fake);
        var outputs = new[] { new IoParamSpec("a") };

        tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs);             // strike one
        fake.ConvertStagedRefuses = false;
        tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs);             // success — counter resets
        fake.ConvertStagedRefuses = true;
        var after = tools.ConvertStaged(FakeBridge.SomeId, "x = 1", outputs); // strike one again

        Assert.DoesNotContain("LEASH", after.Error);
    }

    [Fact]
    public void SetIo_failures_share_the_per_component_counter()
    {
        var fake = new FakeBridge { SetIoThrows = new InvalidOperationException("nope") };
        var tools = new WireifyTools(fake);

        Assert.Throws<McpException>(() =>
            tools.SetIo(FakeBridge.SomeId, new[] { new IoParamSpec("a") }, new[] { new IoParamSpec("b") }));
        fake.ConvertStagedRefuses = true;
        var second = tools.ConvertStaged(FakeBridge.SomeId, "x = 1", new[] { new IoParamSpec("a") });

        Assert.Contains("LEASH", second.Error);
    }

    /// <summary>Every member throws the supplied exception — exercises the Guard path.</summary>
    sealed class ThrowingBridge : IGrasshopperBridge
    {
        readonly Exception _ex;
        public ThrowingBridge(Exception ex) => _ex = ex;
        T Throw<T>() => throw _ex;

        public DocumentSummary GetDocumentSummary(bool includeStagedData = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents, string? nameFilter = null) => Throw<DocumentSummary>();
        public ComponentIntrospection IntrospectComponent(Guid id) => Throw<ComponentIntrospection>();
        public IReadOnlyList<ComponentIntrospection> IntrospectSelected() => Throw<IReadOnlyList<ComponentIntrospection>>();
        public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50) => Throw<InputData>();
        public RuntimeInfo GetRuntimeInfo() => Throw<RuntimeInfo>();
        public ScriptSource GetSource(Guid id) => Throw<ScriptSource>();
        public Guid CreatePythonComponent(PythonRuntime runtime) => Throw<Guid>();
        public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true, bool overwriteExternalEdits = false) => Throw<RuntimeReport?>();
        public void SetParametersFromScript(Guid id) => Throw<object>();
        public WireResult Wire(Guid fromId, int fromOutput, Guid toId, int toInput, WireMode mode = WireMode.Strict) => Throw<WireResult>();
        public ConvertStagedResult ConvertStaged(Guid socketId, string code, IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime, string? nicknameSlug, IReadOnlyList<IoParamSpec>? inputs) => Throw<ConvertStagedResult>();
        public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs) => Throw<ComponentIntrospection>();
        public DeletedComponent DeleteComponent(Guid id) => Throw<DeletedComponent>();
        public PanelText SetPanelText(Guid id, string text) => Throw<PanelText>();
        public RunResult Run(Guid id) => Throw<RunResult>();
        public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false) => Throw<RuntimeReport>();
    }
}
