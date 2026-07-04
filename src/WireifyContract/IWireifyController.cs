// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyContract
{
    /// <summary>
    /// The typed seam the entry assemblies (<c>Wireify.rhp</c> command/panel, <c>WireifyGh.gha</c>
    /// socket) use to drive the isolated WireifyCore: server lifecycle, the Connect flow, session
    /// state, and the status/log/activity events the UI binds. Everything Claude drives (the MCP
    /// tool surface) is deliberately NOT here — this contract is entry-assembly control only.
    ///
    /// Threading: <see cref="Connect"/> blocks (file IO + preflight + terminal spawn) — call it off
    /// the UI thread. Events are raised on background threads; UI subscribers must marshal.
    /// </summary>
    public interface IWireifyController
    {
        /// <summary>Start the loopback MCP server if it is not already running. Idempotent.</summary>
        WireifyServerInfo EnsureServer();

        WireifyServerInfo ServerInfo { get; }

        WireifyConnectionState State { get; }

        /// <summary>Absolute path of the active Grasshopper definition, or null when there is no
        /// canvas, no document, or the document has never been saved.</summary>
        string? ActiveDefinitionPath();

        /// <summary>
        /// One Connect: ensure the server, resolve the definition (null = the active one), scaffold
        /// its home, merge <c>.mcp.json</c>, preflight the Claude CLI, spawn the terminal. Steps
        /// stream through <see cref="ConnectStepCompleted"/> as they complete.
        /// </summary>
        WireifyConnectReport Connect(string? ghFilePath);

        /// <summary>Wireify-managed components on the active canvas (staged sockets + converted ones).</summary>
        WireifyCanvasItem[] DescribeCanvas();

        /// <summary>Recent log lines (bounded), so a panel opened late can render history.</summary>
        IReadOnlyList<WireifyLogLine> RecentLog { get; }

        /// <summary>Folder holding the timestamped connect logs (<c>~/.ify/wireify/logs</c>).</summary>
        string LogsDirectory { get; }

        event Action<WireifyConnectionState> StateChanged;
        event Action<WireifyConnectStep> ConnectStepCompleted;
        event Action<WireifyLogLine> LogEmitted;
        /// <summary>Raised when a Wireify tool starts/stops touching a component (drives the
        /// socket's "Working" state). Args: component InstanceGuid, active.</summary>
        event Action<Guid, bool> ComponentActivityChanged;
    }
}
