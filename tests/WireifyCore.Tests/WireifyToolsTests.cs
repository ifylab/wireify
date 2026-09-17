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
    public void SetSource_solve_false_returns_a_schema_valid_placeholder_report()
    {
        // The result schema declares report required; a null would serialize away and hand
        // every schema-validating client an invalid payload (round-4 defect C). The
        // placeholder is honest: compiled, not solved.
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        var result = tools.SetSource(FakeBridge.SomeId, "a = 1", solve: false);

        Assert.False(result.Solved);
        Assert.NotNull(result.Report);
        var remark = Assert.Single(result.Report!.Messages);
        Assert.Equal("remark", remark.Level);
        Assert.Contains("without solving", remark.Text);
        Assert.Empty(result.Report.Outputs);
    }

    [Fact]
    public void Result_records_give_every_nullable_property_a_default_so_the_schema_marks_it_optional()
    {
        // The S5.6g class: the SDK's generated output schema marks a no-default constructor
        // parameter REQUIRED while its serializer omits nulls — so a nullable-without-default
        // property (ConvertStagedResult.Error was one) makes every payload that nulls it fail
        // client-side validation, masking a mutation that SUCCEEDED. This sweeps the tool
        // result records so the class cannot silently return.
        var resultRecords = new[]
        {
            typeof(ConvertStagedResult), typeof(SetSourceResult), typeof(TypedIoResult),
            typeof(DeletedComponent), typeof(WireResult), typeof(PanelText),
            typeof(RunResult), typeof(RuntimeReport), typeof(InputData),
            typeof(ComponentIntrospection), typeof(AppControlState),
            typeof(ClearBadgeResult), typeof(RenameResult), typeof(ParamInfo),
            typeof(WireifyCore.Hosting.AppSurfaceInfo), typeof(WireifyCore.Hosting.ScaffoldAppResult),
        };
        foreach (var type in resultRecords)
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            foreach (var p in ctor.GetParameters())
            {
                var nullable = !p.ParameterType.IsValueType
                    ? new System.Reflection.NullabilityInfoContext().Create(p).WriteState
                        == System.Reflection.NullabilityState.Nullable
                    : Nullable.GetUnderlyingType(p.ParameterType) is not null;
                if (nullable && p.Name != "Report") // SetSourceResult.Report: required by design, never null (the B19 placeholder)
                    Assert.True(p.HasDefaultValue,
                        $"{type.Name}.{p.Name} is nullable without a default — required-but-omitted when null");
            }
        }
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
        // The csproj pins 0.3.0 and bakes the stamp into the informational version at compile
        // time — file times get rewritten by Unblock-File/copies (live-observed), so only
        // assembly metadata is trustworthy after a zip swap.
        Assert.StartsWith("0.3", WireifyCore.WireifyBuild.Version);
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

        Assert.Contains("CreatePythonComponent:CPython3:-", fake.Calls);
    }

    [Fact]
    public void Create_passes_the_nickname_through()
    {
        // Nicknames are user-facing on the app surface (every card and report heading renders
        // them) — the from-scratch path must be able to name what it creates (round-5 S5.0c).
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        tools.CreatePythonComponent(nickName: "truss-metrics");

        Assert.Contains("CreatePythonComponent:CPython3:truss-metrics", fake.Calls);
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

        Assert.Equal(22, collection.Count()); // 16 loop tools + get_app_info (W-B) + create_control_component + scaffold_app + clear_badge + rename_component + get_document_graph
    }

    [Fact]
    public void GetDocumentGraph_passes_scope_and_caps_through_and_answers_nodes_with_edges()
    {
        // Round-10 S10.6: one read for a definition's wiring instead of one introspect per
        // component. The wrapper hands every knob to the bridge; the edges ARE the wiring.
        var bridge = new FakeBridge();
        var tools = new WireifyTools(bridge);

        var graph = tools.GetDocumentGraph(
            ids: new[] { FakeBridge.SomeId }, includeOutputs: true, maxComponents: 50, nameFilter: "W1");

        Assert.Contains($"GetDocumentGraph:{FakeBridge.SomeId}:True:True:50:W1", bridge.Calls);
        Assert.Equal(2, graph.Nodes.Count);
        var edge = Assert.Single(graph.Edges);
        // Edges address nodes by index; the slider (node 1) feeds W1's x (node 0).
        Assert.Equal(1, edge.From);
        Assert.Equal(0, edge.To);
        Assert.Equal("x", edge.ToParam);
        Assert.Null(edge.ToId);
    }

    [Fact]
    public void ScaffoldApp_runs_the_delegate_for_the_session_home()
    {
        string? scaffolded = null;
        string? shape = null;
        var tools = new WireifyTools(new FakeBridge(),
            appScaffold: (home, template) =>
            {
                scaffolded = home;
                shape = template;
                return new WireifyCore.Hosting.ScaffoldAppResult(
                    true, "http://127.0.0.1:1/app/h/?token=t", true, true, 0, 0,
                    new[] { "index.html", "manifest.json", "theme.css" }, Array.Empty<string>(),
                    true, template);
            });
        var before = WireifySessionContext.CurrentHomeId;
        try
        {
            WireifySessionContext.CurrentHomeId = "home-a";

            var info = tools.ScaffoldApp();

            Assert.Equal("home-a", scaffolded);
            Assert.Equal("panel", shape); // the default page shape
            Assert.True(info.AppDirExists);
            // The receipt reports ACTION, not just state — the S5.4f fix's tool-level shape.
            Assert.Contains("index.html", info.Seeded);
            Assert.True(info.KitStamped);
            Assert.Equal("panel", info.Template);

            tools.ScaffoldApp(template: "report");
            Assert.Equal("report", shape);

            var ex = Assert.Throws<McpException>(() => tools.ScaffoldApp(template: "dashboard"));
            Assert.Contains("panel or report", ex.Message);
        }
        finally { WireifySessionContext.CurrentHomeId = before; }
    }

    [Fact]
    public void ScaffoldApp_refuses_without_a_session_home()
    {
        var tools = new WireifyTools(new FakeBridge(),
            appScaffold: (_, template) => new WireifyCore.Hosting.ScaffoldAppResult(
                true, "", true, true, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), true, template));
        var before = WireifySessionContext.CurrentHomeId;
        try
        {
            WireifySessionContext.CurrentHomeId = null;

            var ex = Assert.Throws<McpException>(() => tools.ScaffoldApp());

            Assert.Contains("scaffold_app failed", ex.Message);
            Assert.Contains("no session home", ex.Message);
        }
        finally { WireifySessionContext.CurrentHomeId = before; }
    }

    [Fact]
    public void ScaffoldApp_refuses_when_no_surface_exists()
    {
        var tools = new WireifyTools(new FakeBridge());

        var ex = Assert.Throws<McpException>(() => tools.ScaffoldApp());

        Assert.Contains("no app surface", ex.Message);
    }

    [Fact]
    public void CreateControlComponent_delegates_the_spec_and_returns_the_state()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);
        var spec = new ControlSpec("slider", "height", Min: 0, Max: 12, Accuracy: "integer", Value: 4);

        var state = tools.CreateControlComponent(spec);

        Assert.Equal("slider", state.Kind);
        Assert.Same(spec, fake.LastControlSpec);
        Assert.Contains("CreateControlComponent:slider:height", fake.Calls);
    }

    [Fact]
    public void CreateControlComponent_requires_a_spec()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        var ex = Assert.Throws<McpException>(() => tools.CreateControlComponent(null!));

        Assert.Contains("create_control_component failed", ex.Message);
        Assert.Contains("spec is required", ex.Message);
        Assert.Empty(fake.Calls);
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
    public void ClearBadge_delegates_and_signals_activity()
    {
        var fake = new FakeBridge();
        var activity = new List<(Guid Id, bool Active)>();
        var tools = new WireifyTools(fake, (id, active) => activity.Add((id, active)));

        var result = tools.ClearBadge(FakeBridge.SomeId);

        Assert.True(result.Cleared);
        Assert.Contains($"ClearBadge:{FakeBridge.SomeId}", fake.Calls);
        Assert.Equal(new[] { (FakeBridge.SomeId, true), (FakeBridge.SomeId, false) }, activity);
    }

    [Fact]
    public void RenameComponent_delegates_and_signals_activity()
    {
        var fake = new FakeBridge();
        var activity = new List<(Guid Id, bool Active)>();
        var tools = new WireifyTools(fake, (id, active) => activity.Add((id, active)));

        var result = tools.RenameComponent(FakeBridge.SomeId, "span");

        Assert.Equal("span", result.NickNameAfter);
        Assert.Equal("slider", result.NickNameBefore);
        Assert.Contains($"RenameComponent:{FakeBridge.SomeId}:span", fake.Calls);
        Assert.Equal(new[] { (FakeBridge.SomeId, true), (FakeBridge.SomeId, false) }, activity);
    }

    [Fact]
    public void RenameComponent_refuses_an_empty_name_before_touching_the_bridge()
    {
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        Assert.Throws<McpException>(() => tools.RenameComponent(FakeBridge.SomeId, "   "));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("RenameComponent"));
    }

    [Fact]
    public void RenameComponent_clear_unnames_through_the_bridge()
    {
        // The deliberate un-name (round-8 F5): clear: true reaches the bridge with an empty
        // name whatever nickName says; without it the empty name stays refused above.
        var fake = new FakeBridge();
        var tools = new WireifyTools(fake);

        var result = tools.RenameComponent(FakeBridge.SomeId, "ignored", clear: true);

        Assert.Equal("", result.NickNameAfter);
        Assert.Contains($"RenameComponent:{FakeBridge.SomeId}:", fake.Calls);
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
        public DocumentGraph GetDocumentGraph(IReadOnlyList<Guid>? ids = null, bool includeParams = true, bool includeOutputs = false,
            int maxComponents = SummaryBounding.DefaultMaxComponents, string? nameFilter = null) => Throw<DocumentGraph>();
        public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50) => Throw<InputData>();
        public RuntimeInfo GetRuntimeInfo() => Throw<RuntimeInfo>();
        public ScriptSource GetSource(Guid id) => Throw<ScriptSource>();
        public Guid CreatePythonComponent(PythonRuntime runtime, string? nickName = null) => Throw<Guid>();
        public AppControlState CreateControlComponent(ControlSpec spec) => Throw<AppControlState>();
        public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true, bool overwriteExternalEdits = false) => Throw<RuntimeReport?>();
        public TypedIoResult SetParametersFromScript(Guid id) => Throw<TypedIoResult>();
        public WireResult Wire(Guid fromId, int fromOutput, Guid toId, int toInput, WireMode mode = WireMode.Strict) => Throw<WireResult>();
        public ConvertStagedResult ConvertStaged(Guid socketId, string code, IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime, string? nicknameSlug, IReadOnlyList<IoParamSpec>? inputs) => Throw<ConvertStagedResult>();
        public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs) => Throw<ComponentIntrospection>();
        public AppState ReadAppState(AppQuery query) => Throw<AppState>();
        public AppGeometry ReadAppGeometry(AppViewRef view) => Throw<AppGeometry>();
        public AppSetResult SetAppControlValue(Guid id, AppPushValue value) => Throw<AppSetResult>();
        public AppControlState SetAppGesture(Guid id, bool open) => Throw<AppControlState>();
        public IDisposable SubscribeSolutionEnd(Func<AppQuery?> queryProvider, Action<AppState> onSolution, Action<string>? onClosed = null,
            Action? onSolveStart = null, Action<AppState>? onActiveChanged = null) => Throw<IDisposable>();
        public DeletedComponent DeleteComponent(Guid id) => Throw<DeletedComponent>();
        public ClearBadgeResult ClearBadge(Guid id) => Throw<ClearBadgeResult>();
        public RenameResult RenameComponent(Guid id, string nickName) => Throw<RenameResult>();
        public PanelText SetPanelText(Guid id, string text) => Throw<PanelText>();
        public RunResult Run(Guid id) => Throw<RunResult>();
        public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false) => Throw<RuntimeReport>();
    }
}
