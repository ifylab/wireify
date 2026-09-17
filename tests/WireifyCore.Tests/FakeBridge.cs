// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

/// <summary>In-memory <see cref="IGrasshopperBridge"/> for tool + host tests: records calls,
/// returns canned DTOs. Lets the MCP layer be exercised end to end without a Rhino install.</summary>
internal sealed class FakeBridge : IGrasshopperBridge
{
    public readonly List<string> Calls = new();
    public static readonly Guid SomeId = new("11111111-1111-1111-1111-111111111111");

    public DocumentSummary GetDocumentSummary(
        bool includeStagedData = false,
        int maxComponents = SummaryBounding.DefaultMaxComponents,
        string? nameFilter = null)
    {
        Calls.Add($"GetDocumentSummary:{includeStagedData}:{maxComponents}:{nameFilter ?? "-"}");
        var staged = includeStagedData
            ? new List<InputData> { new("in1", "list", new TreeInfo(1, 2, true), new List<TypeCount>(), new List<DataSample>()) }
            : null;
        return new DocumentSummary("", new List<ComponentRef>(),
            new List<WireifyComponentInfo> { new(1, SomeId, "W1", "staged", new List<string> { "in1" }, staged) });
    }

    public ComponentIntrospection IntrospectComponent(Guid id)
    { Calls.Add($"IntrospectComponent:{id}"); return new ComponentIntrospection(id, "n", "nn", new List<ParamInfo>(), new List<ParamInfo>()); }

    public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
    { Calls.Add("IntrospectSelected"); return new List<ComponentIntrospection>(); }

    public DocumentGraph GetDocumentGraph(
        IReadOnlyList<Guid>? ids = null,
        bool includeParams = true,
        bool includeOutputs = false,
        int maxComponents = SummaryBounding.DefaultMaxComponents,
        string? nameFilter = null)
    {
        Calls.Add($"GetDocumentGraph:{(ids is null ? "-" : string.Join("|", ids))}:{includeParams}:{includeOutputs}:{maxComponents}:{nameFilter ?? "-"}");
        var other = new Guid("33333333-3333-3333-3333-333333333333");
        return new DocumentGraph(
            "",
            new List<GraphNode>
            {
                new(SomeId, "Python 3 Script", "W1", "component", "Maths", "Script",
                    includeParams ? new List<GraphParam> { new("x", "x", "item", "Number", false, "float") } : null,
                    includeParams ? new List<GraphParam> { new("a", "a", "item", "Number", false) } : null),
                new(other, "Number Slider", "height", "param", "Params", "Input"),
            },
            new List<GraphEdge> { new(1, "height", 0, "x") },
            2);
    }

    public InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50)
    { Calls.Add($"ReadInputData:{id}:{inputParam}:{maxPerBranch}:{maxTotal}"); return new InputData(inputParam, "item", new TreeInfo(0, 0, true), new List<TypeCount>(), new List<DataSample>()); }

    public RuntimeInfo GetRuntimeInfo()
    { Calls.Add("GetRuntimeInfo"); return new RuntimeInfo("8.0", new List<string> { "cpython3" }, "3.9", false, WireifyCore.WireifyBuild.Describe()); }

    /// <summary>Failure knob for the error-contract tests: throw on the next get_source.</summary>
    public Exception? GetSourceThrows;

    public ScriptSource GetSource(Guid id)
    {
        Calls.Add($"GetSource:{id}");
        if (GetSourceThrows is not null) throw GetSourceThrows;
        return new ScriptSource(id, "W1", "a = 1");
    }

    public Guid CreatePythonComponent(PythonRuntime runtime, string? nickName = null)
    { Calls.Add($"CreatePythonComponent:{runtime}:{nickName ?? "-"}"); return SomeId; }

    public ControlSpec? LastControlSpec;

    public AppControlState CreateControlComponent(ControlSpec spec)
    {
        Calls.Add($"CreateControlComponent:{spec.Kind}:{spec.NickName ?? "-"}");
        LastControlSpec = spec;
        return new AppControlState(SomeId, spec.NickName ?? "", spec.Kind, Step: 1);
    }

    public static readonly RuntimeReport CannedReport = new(
        new List<RuntimeMessage> { new("remark", "ok") },
        new List<OutputValue>
        {
            new("a", new TreeInfo(1, 1, true), new List<TypeCount> { new("Integer", "System.Int32", 1) },
                new List<DataSample> { new("{0}", "2", "Integer", 1) }, 1),
        });

    /// <summary>Failure knobs for the leash tests: throw or refuse on the next mutation calls.</summary>
    public Exception? ConvertStagedThrows;
    public bool ConvertStagedRefuses;
    public Exception? SetIoThrows;

    public RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true, bool overwriteExternalEdits = false)
    { Calls.Add($"SetSource:{id}:{source}:{runtime}:{solve}:{overwriteExternalEdits}"); return solve ? CannedReport : null; }

    public TypedIoResult SetParametersFromScript(Guid id)
    {
        Calls.Add($"SetParametersFromScript:{id}");
        return new TypedIoResult(id, false, new[] { "x", "y" }, new[] { "x", "y" });
    }

    /// <summary>Failure knob for the occupancy guard tests: set to simulate an occupied input
    /// (Strict refuses with the protocol message; Replace reports it swapped out).</summary>
    public WireEndInfo? WireOccupiedBy;

    public WireResult Wire(Guid fromId, int fromOutput, Guid toId, int toInput, WireMode mode = WireMode.Strict)
    {
        Calls.Add($"Wire:{fromId}:{fromOutput}:{toId}:{toInput}:{mode}");
        if (WireOccupiedBy is not null && mode == WireMode.Strict)
            throw new InvalidOperationException(Bridge.ErrorProtocol.InputWired("in1", toId, new[] { WireOccupiedBy }));
        var replaced = WireOccupiedBy is not null && mode == WireMode.Replace
            ? new[] { WireOccupiedBy }
            : Array.Empty<WireEndInfo>();
        return new WireResult(fromId, "out", toId, "in1",
            mode.ToString().ToLowerInvariant(), replaced);
    }

    public ConvertStagedResult ConvertStaged(
        Guid socketId, string code, IReadOnlyList<IoParamSpec> outputs,
        PythonRuntime runtime, string? nicknameSlug, IReadOnlyList<IoParamSpec>? inputs)
    {
        Calls.Add($"ConvertStaged:{socketId}:{code}:{string.Join("|", outputs.Select(o => $"{o.Name}/{o.Access}"))}:{runtime}:{nicknameSlug}:{(inputs is null ? "-" : string.Join("|", inputs.Select(i => $"{i.Name}/{i.Access}")))}");
        if (ConvertStagedThrows is not null) throw ConvertStagedThrows;
        if (ConvertStagedRefuses)
            return new ConvertStagedResult(false, Guid.Empty, "W1",
                new List<string>(), new List<string> { "in1" }, new List<string>(), "refused: spec mismatch");
        return new ConvertStagedResult(true, SomeId, "W1", new List<string>(), new List<string>(), new List<string>(), null, CannedReport);
    }

    public ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs)
    {
        Calls.Add($"SetIo:{id}:{inputs.Count}:{outputs.Count}");
        if (SetIoThrows is not null) throw SetIoThrows;
        return new ComponentIntrospection(id, "n", "nn", new List<ParamInfo>(), new List<ParamInfo>());
    }

    public DeletedComponent DeleteComponent(Guid id)
    { Calls.Add($"DeleteComponent:{id}"); return new DeletedComponent(id, "Wireify", "W1"); }

    public ClearBadgeResult ClearBadge(Guid id)
    { Calls.Add($"ClearBadge:{id}"); return new ClearBadgeResult(true, "W1"); }

    public RenameResult RenameComponent(Guid id, string nickName)
    { Calls.Add($"RenameComponent:{id}:{nickName}"); return new RenameResult(id, "slider", nickName); }

    public PanelText SetPanelText(Guid id, string text)
    { Calls.Add($"SetPanelText:{id}:{text}"); return new PanelText(id, "Panel", text.Length); }

    public RunResult Run(Guid id)
    { Calls.Add($"Run:{id}"); return new RunResult(true, 1, CannedReport); }

    public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false)
    { Calls.Add($"ReadRuntimeErrors:{id}:{includeDocument}"); return new RuntimeReport(new List<RuntimeMessage>(), new List<OutputValue>()); }

    // --- Companion app (webapp surface) ---

    public AppState CannedAppState = new(true,
        new List<AppControlState> { new(SomeId, "height", "slider", 5, 0, 10, 1, Step: 0.1) },
        new List<InputData>
        {
            new("tags", "list", new TreeInfo(1, 3, true),
                new List<TypeCount> { new("Text", "System.String", 3) },
                new List<DataSample> { new("{0}", "t1", "Text", 2) }),
        });

    /// <summary>Ambient session id observed on each app-surface bridge call (B3: the app path
    /// must set the session context before every bridge call, subscribe included).</summary>
    public readonly List<string?> AppSessions = new();

    public Exception? ReadAppStateThrows;
    public AppQuery? LastAppQuery;

    public AppState ReadAppState(AppQuery query)
    {
        Calls.Add($"ReadAppState:{string.Join("|", query.Controls)}:"
            + string.Join("|", query.Views.Select(v => v.Param is null ? v.Id.ToString() : $"{v.Id}/{v.Param}")));
        AppSessions.Add(WireifySessionContext.CurrentHomeId);
        LastAppQuery = query;
        if (ReadAppStateThrows is not null) throw ReadAppStateThrows;
        // Mirrors the real bridge: manifest warnings ride the query onto every frame.
        return CannedAppState with { Warnings = query.Warnings };
    }

    public Exception? SetAppControlValueThrows;
    public AppPushValue? LastPush;

    public AppSetResult SetAppControlValue(Guid id, AppPushValue value)
    {
        var shape = value.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? value.Text
            ?? value.Flag?.ToString();
        Calls.Add($"SetAppControlValue:{id}:{shape}");
        AppSessions.Add(WireifySessionContext.CurrentHomeId);
        LastPush = value;
        if (SetAppControlValueThrows is not null) throw SetAppControlValueThrows;
        return new AppSetResult(
            new AppControlState(id, "height", "slider", value.Number ?? 0, 0, 10, 1), false);
    }

    public AppControlState SetAppGesture(Guid id, bool open)
    {
        Calls.Add($"SetAppGesture:{id}:{(open ? "open" : "close")}");
        AppSessions.Add(WireifySessionContext.CurrentHomeId);
        return new AppControlState(id, "height", "slider", 4, 0, 10, 1, Step: 1);
    }

    public AppGeometry CannedGeometry = new(
        "W3 L",
        new[] { new AppMesh(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }) },
        Array.Empty<AppPolyline>(),
        Array.Empty<float>(),
        new double[] { 0, 0, 0, 1, 1, 0 },
        1, 1, 3,
        Array.Empty<string>());

    public AppGeometry ReadAppGeometry(AppViewRef view)
    {
        Calls.Add($"ReadAppGeometry:{view.Id}" + (view.Param is null ? "" : $"/{view.Param}"));
        AppSessions.Add(WireifySessionContext.CurrentHomeId);
        return CannedGeometry;
    }

    /// <summary>The live solution callback + query provider + closed callback the surface
    /// registered — tests fire the callbacks (re-reading the provider like the real bridge does
    /// per solve) to simulate a canvas solve or a closed definition;
    /// <see cref="SolutionUnsubscribed"/> counts disposals.</summary>
    public Action<AppState>? SolutionCallback;
    public Func<AppQuery?>? SolutionQueryProvider;
    public Action<string>? SolutionClosedCallback;
    public Action? SolveStartCallback;
    public Action<AppState>? ActiveChangedCallback;
    public int SolutionUnsubscribed;
    public Exception? SubscribeThrows;

    public IDisposable SubscribeSolutionEnd(
        Func<AppQuery?> queryProvider, Action<AppState> onSolution, Action<string>? onClosed = null,
        Action? onSolveStart = null, Action<AppState>? onActiveChanged = null)
    {
        Calls.Add("SubscribeSolutionEnd");
        AppSessions.Add(WireifySessionContext.CurrentHomeId);
        if (SubscribeThrows is not null) throw SubscribeThrows;
        SolutionQueryProvider = queryProvider;
        SolutionCallback = onSolution;
        SolutionClosedCallback = onClosed;
        SolveStartCallback = onSolveStart;
        ActiveChangedCallback = onActiveChanged;
        return new Unsubscriber(this);
    }

    sealed class Unsubscriber : IDisposable
    {
        readonly FakeBridge _bridge;
        public Unsubscriber(FakeBridge bridge) => _bridge = bridge;
        public void Dispose() => System.Threading.Interlocked.Increment(ref _bridge.SolutionUnsubscribed);
    }
}
