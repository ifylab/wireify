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

        /// <summary>The state of a specific definition's session (by .gh path) — what its socket
        /// button renders, so a freshly opened second definition honestly reads Connect instead of
        /// inheriting another file's live session. Null/unsaved path = no session.</summary>
        WireifyConnectionState StateFor(string? ghFilePath);

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

        /// <summary>The companion-app half of a socket's state for a definition (by .gh path):
        /// home and page existence, the stable tokenless address, the app folder. Cached
        /// briefly — safe on every repaint. Null/unsaved path = no home.</summary>
        WireifyAppStatus AppStatusFor(string? ghFilePath);

        /// <summary>The live per-run app link (token included) for copying — null when the
        /// definition has no page. Minted in memory only; nothing is written anywhere.</summary>
        string? AppLinkFor(string? ghFilePath);

        /// <summary>Open the definition's companion app in the default browser with the live
        /// link: the click is the consent, and the token never touches the canvas or a file.
        /// The step's Ok/Message report the outcome (no page, or the launch failed).</summary>
        WireifyConnectStep OpenApp(string? ghFilePath);

        /// <summary>The definition was saved under a new path (Save As, or a first save). A live
        /// session follows the document instance to the new path; app status caches drop. Safe
        /// to call from every socket on the document — idempotent.</summary>
        void DefinitionRenamed(string? oldPath, string? newPath);

        /// <summary>Another Grasshopper document came to the front (a tab switch, an open, a
        /// close). Drops the app-status cache and raises <see cref="StateChanged"/> so every
        /// per-definition surface — the panel's rows, buttons and registry — re-reads for the
        /// active file (round-11 S11.27: the panel kept showing, and Open home kept acting on,
        /// the previous definition until it was reopened).</summary>
        void ActiveDefinitionChanged();

        /// <summary>Recent log lines (bounded), so a panel opened late can render history.</summary>
        IReadOnlyList<WireifyLogLine> RecentLog { get; }

        /// <summary>The last Build's steps for a definition (by .gh path), oldest first — so a
        /// panel opened after the Build, or one the socket drove, renders the walk instead of
        /// dashes. Empty when no Build ran for that file in this Rhino session.</summary>
        IReadOnlyList<WireifyConnectStep> RecentStepsFor(string? ghFilePath);

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
