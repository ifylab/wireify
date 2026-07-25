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

    /// <summary><c>TotalObjectCount</c> is the true document size; when the components list was
    /// capped for a big canvas, <c>ComponentsTruncated</c> is true (selected and Wireify-managed
    /// objects are kept preferentially; the <c>Wireify</c> registry itself is never truncated).
    /// <c>IsActiveCanvas</c> is false when this session's definition is open but not the front
    /// tab — reads keep working there; mutations wait for it to be brought to front.</summary>
    public sealed record DocumentSummary(
        string? ActiveFilePath,
        IReadOnlyList<ComponentRef> Components,
        IReadOnlyList<WireifyComponentInfo>? Wireify = null,
        int TotalObjectCount = 0,
        bool ComponentsTruncated = false,
        bool IsActiveCanvas = true);

    /// <summary>One end of a live wire: the document object a param connects to (its owning
    /// component, or the floating param itself) plus the param's name.</summary>
    public sealed record WireEndInfo(Guid ComponentId, string NickName, string Param);

    /// <summary><c>Hint</c> is the selected type hint on a script variable param ("" when none or
    /// not a script param) — <c>TypeName</c> never reflects hints, so this is the honest echo after
    /// a hint change. <c>AvailableHints</c> lists what the param's hint registry offers (script
    /// params only; null elsewhere). <c>Sources</c> (what feeds an input) and <c>Recipients</c>
    /// (what consumes an output) are the param's LIVE wiring — null when none, capped at 50 each;
    /// <c>SourceCount</c>/<c>RecipientCount</c> always carry the true totals, so a mega-fanout is
    /// visible without flooding the payload.</summary>
    public sealed record ParamInfo(
        string Name,
        string NickName,
        string Access,
        string TypeName,
        bool Optional,
        string Hint = "",
        IReadOnlyList<string>? AvailableHints = null,
        IReadOnlyList<WireEndInfo>? Sources = null,
        IReadOnlyList<WireEndInfo>? Recipients = null,
        int SourceCount = 0,
        int RecipientCount = 0);

    public sealed record ComponentIntrospection(
        Guid Id,
        string Name,
        string NickName,
        IReadOnlyList<ParamInfo> Inputs,
        IReadOnlyList<ParamInfo> Outputs);

    public sealed record TreeInfo(int PathCount, int DataCount, bool IsFlat);

    /// <summary><c>Clr</c> is the unwrapped native type (what a type hint or <c>.Value</c> yields);
    /// <c>Goo</c> is the Grasshopper wrapper class the script actually receives on an un-hinted
    /// param (e.g. <c>GH_Line</c>). Empty when the item is not goo-wrapped.</summary>
    public sealed record TypeCount(string TypeName, string Clr, int Count, string Goo = "");

    /// <summary><c>ValueLength</c> is the full length of the value's string form; <c>Value</c>
    /// itself is capped (a huge wire value must not flood the response). A length of exactly
    /// 32767 is the classic upstream-clip signature (GH panels truncate pasted text).</summary>
    public sealed record DataSample(string Path, string Value, string TypeName, int ValueLength = 0);

    /// <summary>
    /// The edge: a live read of the data on one wired input after a solve — tree shape, a type
    /// histogram, and capped samples — so generated Python can be typed to what is actually flowing.
    /// <c>Warnings</c> carries data-health flags (today: a text value at exactly the panel clip
    /// length, i.e. truncated upstream).
    /// </summary>
    public sealed record InputData(
        string Param,
        string Access,
        TreeInfo Tree,
        IReadOnlyList<TypeCount> Types,
        IReadOnlyList<DataSample> Samples,
        IReadOnlyList<string>? Warnings = null);

    /// <summary><c>RhinoCodeLoaded</c>: whether the RhinoCode/script assemblies are already loaded
    /// in this Rhino. False means the first Python-component create triggers their lazy
    /// initialisation — a pause on healthy installs, a known crash spot on fragile ones — so the
    /// agent can warn the user before the first create/convert. <c>WireifyBuild</c> is the loaded
    /// plugin build ("0.2.0 build 2026-07-09 09:47") — the mechanical answer to "did the swap
    /// take?" when a session's observed tool behavior contradicts the expected feature set.</summary>
    public sealed record RuntimeInfo(
        string RhinoVersion,
        IReadOnlyList<string> AvailableRuntimes,
        string PythonVersion,
        bool RhinoCodeLoaded = false,
        string WireifyBuild = "");

    /// <summary>A script component's current source, as read from the component (the port flow's
    /// first step: read the legacy code before regenerating it).</summary>
    public sealed record ScriptSource(Guid Id, string NickName, string Source);

    /// <summary>Receipt for a delete: which Wireify-managed object was removed. The removal is one
    /// undo record, so ctrl-Z in Grasshopper restores the object and its wires.</summary>
    public sealed record DeletedComponent(Guid Id, string Name, string NickName);

    /// <summary>Receipt for a panel write: which Panel now holds text of what length. One undo
    /// record — ctrl-Z restores the previous content.</summary>
    public sealed record PanelText(Guid Id, string NickName, int Length);

    /// <summary>How <c>wire</c> treats a target input that already has sources: <c>Strict</c>
    /// (default) refuses — an occupied input is never touched without an explicit choice;
    /// <c>Replace</c> swaps the existing wire(s) out; <c>Add</c> merges deliberately (branches
    /// combine).</summary>
    public enum WireMode
    {
        Strict,
        Add,
        Replace,
    }

    /// <summary>Receipt for a wire: what connected where (param names resolved from the indexes),
    /// in which mode, as one undo record. <c>ReplacedSources</c> lists the wire ends Replace
    /// removed (empty otherwise) — the agent verifies what happened without a follow-up read.</summary>
    public sealed record WireResult(
        Guid FromId,
        string FromOutput,
        Guid ToId,
        string ToInput,
        string Mode,
        IReadOnlyList<WireEndInfo> ReplacedSources);

    public sealed record RuntimeMessage(string Level, string Text);

    /// <summary>One output in a runtime report, shaped exactly like a read input: tree stats, a
    /// type histogram, and capped samples (each carrying its full <c>ValueLength</c>), plus the
    /// true total <c>Count</c> — so tree preservation is verifiable from the report itself and a
    /// heavy output can never flood the response or pin the UI thread.</summary>
    public sealed record OutputValue(
        string Param,
        TreeInfo Tree,
        IReadOnlyList<TypeCount> Types,
        IReadOnlyList<DataSample> Samples,
        int Count,
        IReadOnlyList<string>? Warnings = null);

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
    /// <c>TypeHint</c> is an optional hint token from the component's own registry.
    /// </summary>
    public sealed record IoParamSpec(
        [property: Description("Variable name the script sees (for staged sockets: the staged input name).")] string Name,
        [property: Description("Data access: item, list, or tree.")] string Access = "item",
        [property: Description("Optional type-hint token — .NET-style names: string, double, int, bool, Line, Curve, Point3d, Brep ... (NOT Python names; 'str'/'float' do not exist). A wrong token fails loudly listing the real ones. On convert_staged inputs, omitted hints auto-select from the live wired data.")] string? TypeHint = null);

    /// <summary>
    /// Result of converting a staged socket into a stock Python component. On a refusal (bad
    /// argument shape vs the staged inputs) the conversion makes NO changes: <c>Converted</c> is
    /// false, <c>Error</c> says why, and <c>ScriptInputs</c> reports the staged input names, so
    /// the caller can fix the arguments and call again. <c>Warnings</c> carries data-shape flags
    /// worth relaying to the user before building on the result — today: a mixed-type input tree
    /// no hint can fit (its geometry reaches the script as script-doc Guid references).
    /// </summary>
    public sealed record ConvertStagedResult(
        bool Converted,
        Guid NewComponentId,
        string NickName,
        IReadOnlyList<string> WiredInputs,
        IReadOnlyList<string> ScriptInputs,
        IReadOnlyList<string> Outputs,
        string? Error,
        RuntimeReport? Report = null,
        IReadOnlyList<string>? Warnings = null);
}
