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
        return new DocumentSummary(null, new List<ComponentRef>(),
            new List<WireifyComponentInfo> { new(1, SomeId, "W1", "staged", new List<string> { "in1" }, staged) });
    }

    public ComponentIntrospection IntrospectComponent(Guid id)
    { Calls.Add($"IntrospectComponent:{id}"); return new ComponentIntrospection(id, "n", "nn", new List<ParamInfo>(), new List<ParamInfo>()); }

    public IReadOnlyList<ComponentIntrospection> IntrospectSelected()
    { Calls.Add("IntrospectSelected"); return new List<ComponentIntrospection>(); }

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

    public Guid CreatePythonComponent(PythonRuntime runtime)
    { Calls.Add($"CreatePythonComponent:{runtime}"); return SomeId; }

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

    public void SetParametersFromScript(Guid id)
    { Calls.Add($"SetParametersFromScript:{id}"); }

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

    public PanelText SetPanelText(Guid id, string text)
    { Calls.Add($"SetPanelText:{id}:{text}"); return new PanelText(id, "Panel", text.Length); }

    public RunResult Run(Guid id)
    { Calls.Add($"Run:{id}"); return new RunResult(true, 1, CannedReport); }

    public RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false)
    { Calls.Add($"ReadRuntimeErrors:{id}:{includeDocument}"); return new RuntimeReport(new List<RuntimeMessage>(), new List<OutputValue>()); }
}
