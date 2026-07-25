// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using WireifyContract;
using WireifyCore.Bridge;
using WireifyCore.Connect;
using WireifyCore.Mcp;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// The one static the entry assemblies reflect once across the ALC boundary. Everything after
    /// this call is typed: the returned object implements <see cref="IWireifyController"/> from the
    /// shared contract assembly (loaded in the Default context, deferred by the isolated one).
    /// </summary>
    public static class WireifyEntryPoint
    {
        public static IWireifyController CreateController() => WireifyController.Instance;
    }

    /// <summary>
    /// The session controller behind <see cref="IWireifyController"/>: owns the MCP host (bridge +
    /// tools + HttpListener), runs the Connect flow, tracks connection state (listening -> launched
    /// -> connected on the first authenticated request), and feeds the panel/socket UIs through
    /// events. Lives inside the isolated load context; compile-checked here, exercised in Rhino.
    /// </summary>
    public sealed class WireifyController : IWireifyController
    {
        public static WireifyController Instance { get; } = new WireifyController(new SystemTerminalLauncher());

        const int MaxLogLines = 400;

        readonly object _gate = new();
        readonly ITerminalLauncher _launcher;
        readonly IUiInvoker _ui = new RhinoUiInvoker();
        readonly List<WireifyLogLine> _log = new();
        readonly SessionLogWriter _sessionLog;
        // One session per Connected definition: document binding for the bridge's routing, state
        // for that definition's socket/panel, terminal liveness per session — so a second open
        // definition never reads (or mutates) another file's session.
        readonly SessionRegistry _sessions = new();

        WireifyMcpHost? _host;
        WireifyConnectionState _serverState = WireifyConnectionState.ServerStopped;
        bool _loggedLegacyAuth;

        internal WireifyController(ITerminalLauncher launcher)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            // The writer's health notice goes to the panel + console only — routing it through
            // Log would feed it back into the writer it is reporting about.
            _sessionLog = new SessionLogWriter(new WireifyPaths().LogsDir, DateTime.Now, SessionLogNotice);
        }

        public event Action<WireifyConnectionState>? StateChanged;
        public event Action<WireifyConnectStep>? ConnectStepCompleted;
        public event Action<WireifyLogLine>? LogEmitted;
        public event Action<Guid, bool>? ComponentActivityChanged;

        /// <summary>The global dot: the server level or the most-advanced session, whichever is
        /// higher — "something is live". Per-definition truth is <see cref="StateFor"/>.</summary>
        public WireifyConnectionState State
        {
            get
            {
                var sessions = _sessions.MaxState;
                lock (_gate) return sessions > _serverState ? sessions : _serverState;
            }
        }

        /// <summary>The state of THIS definition's session (by .gh path) — what its socket button
        /// renders. No session for the path = the server level (so the button reads Connect).</summary>
        public WireifyConnectionState StateFor(string? ghFilePath)
        {
            var session = _sessions.StateFor(ghFilePath);
            lock (_gate) return session > _serverState ? session : _serverState;
        }

        public WireifyServerInfo ServerInfo
        {
            get
            {
                lock (_gate)
                {
                    return _host is { } h
                        ? new WireifyServerInfo(h.Port, $"http://127.0.0.1:{h.Port}/mcp", h.IsListening, WireifyBuild.Describe())
                        : new WireifyServerInfo(0, "", false, WireifyBuild.Describe());
                }
            }
        }

        public IReadOnlyList<WireifyLogLine> RecentLog
        {
            get { lock (_gate) return _log.ToArray(); }
        }

        public string LogsDirectory => new WireifyPaths().LogsDir;

        public WireifyServerInfo EnsureServer()
        {
            var started = false;
            WireifyServerInfo info;
            lock (_gate)
            {
                if (_host is null)
                {
                    var secret = Guid.NewGuid().ToString("N"); // per-Rhino-run; lands in .mcp.json at Connect
                    // Calls route to the calling session's document (the X-Wireify-Home header,
                    // snapshotted per serialized call by the marshalling seam), so a session can
                    // never touch another definition just because its canvas is in front. Clients
                    // without a session header keep the legacy active-document behavior.
                    var resolver = new SessionDocumentResolver(ActiveDocument, _sessions.Binding);
                    var bridge = new MarshallingBridge(
                        new GrasshopperBridge(resolver), _ui,
                        (message, ok) => Log("[wireify]", message, ok),
                        callContext: resolver.SetCallContext,
                        // File-only: the panel already shows each outcome; the file needs the
                        // entry too, so a call that never returns is visible in a post-mortem.
                        entryLog: tool => _sessionLog.Append(
                            new WireifyLogLine(DateTime.Now, "[wireify]", $"→ {tool}", true)));
                    var tools = new WireifyTools(bridge, OnToolActivity);
                    _host = new WireifyMcpHost(tools, secret);
                    _host.AuthenticatedRequest += OnAuthenticatedRequest;
                    _host.Start(WireifyIds.DefaultPort);
                    if (_serverState == WireifyConnectionState.ServerStopped) _serverState = WireifyConnectionState.ServerListening;
                    started = true;
                }
                info = new WireifyServerInfo(_host.Port, $"http://127.0.0.1:{_host.Port}/mcp", _host.IsListening, WireifyBuild.Describe());
            }

            if (started)
            {
                StateChanged?.Invoke(WireifyConnectionState.ServerListening);
                // The build identity leads the line: after a zip swap this is the ten-second
                // proof of which build actually loaded (the round-17 stale-install lesson).
                Log("[wireify]", $"Wireify {info.Build} — MCP server listening on {info.Url}", true);
                foreach (var warning in InstallLocations.Warnings(
                    typeof(WireifyController).Assembly.Location, InstallLocations.ExistingRoots()))
                    Log("[wireify]", warning, false);
                foreach (var warning in DuplicateAssemblyWarnings())
                    Log("[wireify]", warning, false);
            }
            return info;
        }

        public string? ActiveDefinitionPath()
            => _ui.Invoke(() => Instances.ActiveCanvas?.Document?.FilePath) is { Length: > 0 } p ? p : null;

        public WireifyConnectReport Connect(string? ghFilePath)
        {
            try
            {
                var server = EnsureServer();

                var path = ghFilePath;
                if (string.IsNullOrEmpty(path))
                {
                    var (hasDoc, activePath) = _ui.Invoke(() =>
                    {
                        var doc = Instances.ActiveCanvas?.Document;
                        return (doc != null, doc?.FilePath);
                    });
                    if (!hasDoc)
                        return Refuse(server.Port, "no active Grasshopper definition",
                            "Open a Grasshopper definition, then Connect again.");
                    if (string.IsNullOrEmpty(activePath))
                        return Refuse(server.Port, "the definition is unsaved",
                            "Wireify keys the agent home to the .gh file path. Save the definition, then Connect again.");
                    path = activePath;
                }

                WireifyMcpHost host;
                lock (_gate) host = _host!;

                var connector = new WireifyConnector(
                    new WireifyPaths(),
                    new HomeScaffolder(ResolveTemplateRoot()),
                    _launcher);
                var result = connector.Connect(path!, host, OnConnectStep);

                // Register (or refresh) this definition's session: the document binding routes
                // its tool calls, the per-session state drives ITS sockets only. Registered even
                // when the terminal spawn failed — a manually opened terminal in the home still
                // authenticates with the session header and routes correctly.
                if (!string.IsNullOrEmpty(result.HomeDir))
                {
                    var homeId = Path.GetFileName(result.HomeDir);
                    _sessions.Register(homeId, path!, FindOpenDocumentId(path!), result.Terminal, result.TerminalLaunched);
                    if (result.Terminal is { } handle)
                    {
                        handle.Exited += () => OnSessionTerminalExited(handle);
                        if (handle.HasExited) OnSessionTerminalExited(handle); // died before the subscription
                    }
                    StateChanged?.Invoke(State);
                }

                var success = result.Steps.All(s => s.Ok);
                var hint = BuildHint(result);
                return new WireifyConnectReport(
                    success, result.Port, result.HomeDir, result.McpConfigPath,
                    result.Preflight.ClaudeFound, result.TerminalLaunched,
                    result.Steps.Select(s => new WireifyConnectStep(s.Scope, s.Message, s.Ok, s.Kind)).ToArray(),
                    hint);
            }
            catch (Exception ex)
            {
                var step = new WireifyConnectStep("[wireify]", $"connect failed: {ex.Message}", false, "error");
                RaiseConnectStep(step);
                return new WireifyConnectReport(false, ServerInfo.Port, "", "", false, false,
                    new[] { step }, $"Unexpected failure — see the Wireify log. {ex.Message}");
            }
        }

        public WireifyCanvasItem[] DescribeCanvas() => _ui.Invoke(() =>
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc is null) return Array.Empty<WireifyCanvasItem>();

            var items = new List<WireifyCanvasItem>();
            foreach (var obj in doc.Objects)
            {
                if (obj is not IGH_Component comp) continue;
                if (comp.ComponentGuid == WireifyIds.SocketComponentGuid)
                {
                    WireifyIds.TryParseNumber(comp.NickName, out var n);
                    items.Add(new WireifyCanvasItem(
                        comp.InstanceGuid, n, comp.NickName ?? "", converted: false,
                        comp.Params.Input.Select(p => p.NickName ?? "").ToArray()));
                }
                else if (WireifyIds.TryParseNumber(comp.NickName, out var n2))
                {
                    items.Add(new WireifyCanvasItem(
                        comp.InstanceGuid, n2, comp.NickName ?? "", converted: true,
                        Array.Empty<string>()));
                }
            }
            return items.OrderBy(i => i.Number).ToArray();
        });

        // --- internals -----------------------------------------------------------------------

        static GH_Document? ActiveDocument() => Instances.ActiveCanvas?.Document;

        void OnConnectStep(Connect.ConnectStep s)
            => RaiseConnectStep(new WireifyConnectStep(s.Scope, s.Message, s.Ok, s.Kind));

        /// <summary>Every connect step goes to the panel event, the session log, AND Rhino's
        /// command line — the console is where users actually look, and an adoption failure that
        /// only ever landed in connect-*.log cost two test rounds before anyone saw it.</summary>
        void RaiseConnectStep(WireifyConnectStep step)
        {
            ConnectStepCompleted?.Invoke(step);
            Log(step.Scope, step.Message, step.Ok);
            try { Rhino.RhinoApp.WriteLine($"{step.Scope} {(step.Ok ? "ok " : "ERR")} {step.Message}"); }
            catch { /* echo is best-effort (headless hosts have no console) */ }
        }

        void OnToolActivity(Guid id, bool active) => ComponentActivityChanged?.Invoke(id, active);

        /// <summary>The session-log writer's health line: panel buffer + console, never the
        /// session file itself (that is the component being reported about).</summary>
        void SessionLogNotice(string message, bool ok)
        {
            var line = new WireifyLogLine(DateTime.Now, "[wireify]", message, ok);
            lock (_gate)
            {
                _log.Add(line);
                if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
            LogEmitted?.Invoke(line);
            try { Rhino.RhinoApp.WriteLine($"[wireify] {(ok ? "ok " : "ERR")} {message}"); }
            catch { /* echo is best-effort (headless hosts have no console) */ }
        }

        /// <summary>More than one loaded copy of a Wireify assembly means two installs are live
        /// (the path-level check above can miss a copy that loaded before us). In that state
        /// exception-type identity breaks across contexts and every tool failure surfaces as the
        /// MCP SDK's generic mask instead of the named error — one line here is the ten-second
        /// diagnosis for an otherwise invisible failure mode.</summary>
        static IEnumerable<string> DuplicateAssemblyWarnings()
        {
            var warnings = new List<string>();
            try
            {
                var groups = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name?.StartsWith("Wireify", StringComparison.OrdinalIgnoreCase) == true)
                    .GroupBy(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    if (group.Count() < 2) continue;
                    var locations = group
                        .Select(a => { try { return a.Location; } catch { return ""; } })
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    warnings.Add($"{group.Key} is loaded {group.Count()} times"
                        + (locations.Count > 0 ? $" ({string.Join(" | ", locations)})" : "")
                        + " — two Wireify installs are live; tool errors can lose their detail in this state. "
                        + "Remove the extra install (see the dual-install row in the README troubleshooting).");
                }
            }
            catch { /* diagnostics only */ }
            return warnings;
        }

        void Log(string scope, string message, bool ok)
        {
            var line = new WireifyLogLine(DateTime.Now, scope, message, ok);
            lock (_gate)
            {
                _log.Add(line);
                if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
            _sessionLog.Append(line); // panel buffer dies with Rhino; the file survives for post-mortems
            LogEmitted?.Invoke(line);
        }

        void OnAuthenticatedRequest(string? session)
        {
            if (session is null)
            {
                // No session header: a hand-run/debug client (every Wireify-spawned terminal
                // carries the header via its home's .mcp.json). Active-document routing, logged once.
                bool logIt;
                lock (_gate) { logIt = !_loggedLegacyAuth; _loggedLegacyAuth = true; }
                if (logIt)
                    Log("[wireify]", "authenticated request without a session header (legacy or debug client) — active-document routing", true);
                return;
            }
            var file = _sessions.MarkAuthenticated(session);
            if (file is null) return; // already connected, or a session this server never registered
            StateChanged?.Invoke(State);
            Log("[wireify]", $"Claude connected ({file})", true);
        }

        /// <summary>A session's terminal closed: demote THAT session to ServerListening so ITS
        /// definition's sockets read Connect again and its auth transition re-arms — other
        /// definitions' sessions are untouched. A superseded handle (the session re-Connected)
        /// is ignored.</summary>
        void OnSessionTerminalExited(ITerminalHandle handle)
        {
            var file = _sessions.HandleExit(handle);
            if (file is null) return;
            StateChanged?.Invoke(State);
            Log("[wireify]", $"Claude terminal closed ({file}) — Connect (or right-click a socket) launches a new one", true);
        }

        /// <summary>The open document whose file path matches — its id makes the session binding
        /// survive a mid-session SaveAs (the path alone would go stale until the next Connect).</summary>
        Guid FindOpenDocumentId(string ghFilePath) => _ui.Invoke(() =>
        {
            var server = Instances.DocumentServer;
            if (server is null) return Guid.Empty;
            foreach (var entry in (System.Collections.IEnumerable)server)
            {
                if (entry is not GH_Document doc || string.IsNullOrEmpty(doc.FilePath)) continue;
                if (string.Equals(
                        Path.GetFullPath(doc.FilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(ghFilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    return doc.DocumentID;
            }
            return Guid.Empty;
        });

        WireifyConnectReport Refuse(int port, string reason, string hint)
        {
            var step = new WireifyConnectStep("[wireify]", reason, false, "refused");
            RaiseConnectStep(step);
            return new WireifyConnectReport(false, port, "", "", false, false, new[] { step }, hint);
        }

        static string? BuildHint(ConnectResult result)
        {
            if (!result.Preflight.ClaudeFound)
                return "Claude Code CLI not found. Install it — macOS: curl -fsSL https://claude.ai/install.sh | bash — "
                     + "Windows (PowerShell): irm https://claude.ai/install.ps1 | iex — then run claude once and sign in. "
                     + "Requires a Pro/Max/Team/Enterprise plan or a Console API account; free claude.ai accounts cannot run Claude Code.";
            if (!result.TerminalLaunched)
                return $"The terminal could not be opened. Open one yourself in {result.HomeDir} and run: claude";
            return "First time on this definition: approve the 'wireify' MCP server when Claude asks (one keypress). "
                 + "If nothing happens, check the terminal window.";
        }

        static string ResolveTemplateRoot()
        {
            var dir = Path.GetDirectoryName(typeof(WireifyController).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(dir!, "home-template"),
                    Path.Combine(dir!, "..", "home-template"),
                })
                {
                    if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            throw new DirectoryNotFoundException(
                "home-template folder not found beside the Wireify plugin (packaging issue).");
        }
    }
}
