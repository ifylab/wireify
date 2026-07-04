// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace WireifyCore.Bridge
{
    /// <summary>Which Grasshopper Python runtime a component targets.</summary>
    public enum PythonRuntime
    {
        CPython3,
        IronPython2,
    }

    // Plain DTOs returned across the bridge boundary. They carry no Grasshopper types, so the MCP
    // layer can document them as tool output schemas and the test layer can assert on them without
    // a Rhino install.

    public sealed record ComponentRef(Guid Id, string Name, string NickName);

    /// <summary>A Wireify-managed component: a staged socket ("staged") or a converted, now-stock
    /// Python component ("converted") still carrying its <c>W&lt;n&gt;</c> nickname. The document
    /// itself is the registry — numbers come from the nickname convention.</summary>
    public sealed record WireifyComponentInfo(
        int Number,
        Guid Id,
        string NickName,
        string State,
        IReadOnlyList<string> InputNames,
        IReadOnlyList<InputData>? StagedData = null);

    public sealed record DocumentSummary(
        string? ActiveFilePath,
        IReadOnlyList<ComponentRef> Components,
        IReadOnlyList<WireifyComponentInfo>? Wireify = null);

    public sealed record ParamInfo(
        string Name,
        string NickName,
        string Access,
        string TypeName,
        bool Optional);

    public sealed record ComponentIntrospection(
        Guid Id,
        string Name,
        string NickName,
        IReadOnlyList<ParamInfo> Inputs,
        IReadOnlyList<ParamInfo> Outputs);

    public sealed record TreeInfo(int PathCount, int DataCount, bool IsFlat);

    public sealed record TypeCount(string TypeName, string Clr, int Count);

    public sealed record DataSample(string Path, string Value, string TypeName);

    /// <summary>
    /// The edge: a live read of the data on one wired input after a solve — tree shape, a type
    /// histogram, and capped samples — so generated Python can be typed to what is actually flowing.
    /// </summary>
    public sealed record InputData(
        string Param,
        string Access,
        TreeInfo Tree,
        IReadOnlyList<TypeCount> Types,
        IReadOnlyList<DataSample> Samples);

    /// <summary><c>RhinoCodeLoaded</c>: whether the RhinoCode/script assemblies are already loaded
    /// in this Rhino. False means the first Python-component create triggers their lazy
    /// initialisation — a pause on healthy installs, a known crash spot on fragile ones — so the
    /// agent can warn the user before the first create/convert.</summary>
    public sealed record RuntimeInfo(
        string RhinoVersion,
        IReadOnlyList<string> AvailableRuntimes,
        string PythonVersion,
        bool RhinoCodeLoaded = false);

    /// <summary>A script component's current source, as read from the component (the port flow's
    /// first step: read the legacy code before regenerating it).</summary>
    public sealed record ScriptSource(Guid Id, string NickName, string Source);

    public sealed record RuntimeMessage(string Level, string Text);

    public sealed record OutputValue(string Param, IReadOnlyList<string> Values);

    public sealed record RuntimeReport(
        IReadOnlyList<RuntimeMessage> Messages,
        IReadOnlyList<OutputValue> Outputs);

    /// <summary>Solve outcome plus the post-solve report, so the caller never needs a separate
    /// read to learn what the solve it just triggered said.</summary>
    public sealed record RunResult(bool Ran, int RunCount, RuntimeReport? Report = null);

    /// <summary>Result of set_source: the component solved by default after compiling, and
    /// <c>Report</c> carries that solve's messages + outputs (null when <c>solve</c> was false).</summary>
    public sealed record SetSourceResult(Guid Id, bool Solved, RuntimeReport? Report);

    /// <summary>
    /// One scripted input or output parameter, declared explicitly (the McNeel-sanctioned
    /// <c>ScriptVariableParam</c> path — plain script-mode components never derive inputs from
    /// source). <c>Name</c> is the variable the script sees; <c>Access</c> is item/list/tree;
    /// <c>TypeHint</c> is an optional hint name (best-effort, e.g. "float", "Curve").
    /// </summary>
    public sealed record IoParamSpec(
        [property: Description("Variable name the script sees (for staged sockets: the staged input name).")] string Name,
        [property: Description("Data access: item, list, or tree.")] string Access = "item",
        [property: Description("Optional type-hint name, e.g. float, str, Curve, Point3d. Omit for generic.")] string? TypeHint = null);

    /// <summary>
    /// Result of converting a staged socket into a stock Python component. On a refusal (bad
    /// argument shape vs the staged inputs) the conversion makes NO changes: <c>Converted</c> is
    /// false, <c>Error</c> says why, and <c>ScriptInputs</c> reports the staged input names, so
    /// the caller can fix the arguments and call again.
    /// </summary>
    public sealed record ConvertStagedResult(
        bool Converted,
        Guid NewComponentId,
        string NickName,
        IReadOnlyList<string> WiredInputs,
        IReadOnlyList<string> ScriptInputs,
        IReadOnlyList<string> Outputs,
        string? Error,
        RuntimeReport? Report = null);
}
