// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// The seam between the MCP tool surface and the live Grasshopper document. Every method maps
    /// to the validated spike recipe (create -> set source -> compile -> type -> wire -> run ->
    /// read). The MCP layer marshals these calls onto the GH UI thread; this interface itself is
    /// thread-agnostic and Rhino-type-free at its boundary.
    /// </summary>
    public interface IGrasshopperBridge
    {
        // --- Orientation (read-only) ---

        /// <summary>Document listing plus the Wireify registry. With
        /// <paramref name="includeStagedData"/> the staged sockets' wired inputs carry their live
        /// data (same shaping + caps as <see cref="ReadInputData"/>) — orientation in one call.</summary>
        DocumentSummary GetDocumentSummary(bool includeStagedData = false);

        ComponentIntrospection IntrospectComponent(Guid id);

        IReadOnlyList<ComponentIntrospection> IntrospectSelected();

        /// <summary>Live read of one wired input after the last solve (the edge).</summary>
        InputData ReadInputData(Guid id, string inputParam, int maxPerBranch = 5, int maxTotal = 50);

        RuntimeInfo GetRuntimeInfo();

        /// <summary>Read a script component's current source (Rhino 8 script components and legacy
        /// GhPython alike) — the port flow's step one.</summary>
        ScriptSource GetSource(Guid id);

        // --- Build (mutation) ---

        /// <summary>Emit a fresh Python component of the given runtime and add it to the document.</summary>
        Guid CreatePythonComponent(PythonRuntime runtime);

        /// <summary>
        /// Inject generated source and recompile. On CPython 3 the <c>#! python 3</c> directive is
        /// prepended if absent (RH-96540 drops the language spec without it); omitted for IronPython 2.
        /// With <paramref name="solve"/> (the default) the component is solved after compiling and
        /// the post-solve report returned — outputs are never stale after a revision.
        /// </summary>
        RuntimeReport? SetSource(Guid id, string source, PythonRuntime runtime, bool solve = true);

        /// <summary>Auto-build typed I/O params from the script's variables (validated path).</summary>
        void SetParametersFromScript(Guid id);

        /// <summary>Connect <paramref name="fromOutput"/> of one component into <paramref name="toInput"/> of another.</summary>
        void Wire(Guid fromId, int fromOutput, Guid toId, int toInput);

        /// <summary>
        /// Convert a staged Wireify socket into a stock Python component in place: create at the
        /// socket's pivot, build the input/output params EXPLICITLY (inputs = the staged names;
        /// outputs = the given specs; the sanctioned ScriptVariableParam path — no script parsing),
        /// set source, migrate wires by name, keep the W-number nickname, remove the socket — one
        /// undo step. Refuses (no changes) when the specs do not line up with the staged inputs.
        /// </summary>
        ConvertStagedResult ConvertStaged(
            Guid socketId,
            string code,
            IReadOnlyList<IoParamSpec> outputs,
            PythonRuntime runtime,
            string? nicknameSlug,
            IReadOnlyList<IoParamSpec>? inputs);

        /// <summary>
        /// Define a script component's I/O explicitly (same ScriptVariableParam mechanism):
        /// replaces its variable params with the given specs (the stdout "out" param is kept),
        /// preserving wires on inputs whose name survives. Returns the resulting introspection.
        /// </summary>
        ComponentIntrospection SetIo(Guid id, IReadOnlyList<IoParamSpec> inputs, IReadOnlyList<IoParamSpec> outputs);

        // --- Run + read ---

        RunResult Run(Guid id);

        RuntimeReport ReadRuntimeErrors(Guid id, bool includeDocument = false);
    }
}
