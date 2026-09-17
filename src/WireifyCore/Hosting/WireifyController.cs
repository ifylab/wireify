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
        // The last Build's steps per definition, for a panel opened after the fact (S10.5).
        readonly StepLedger _steps = new();

        WireifyMcpHost? _host;
        AppSurface? _appSurface;
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

        public IReadOnlyList<WireifyConnectStep> RecentStepsFor(string? ghFilePath) => _steps.For(ghFilePath);

        public string LogsDirectory => new WireifyPaths().LogsDir;

        // The socket asks on every repaint; the answer is three file-system checks, so it is
        // held for a moment per path and dropped whenever this process changes the answer
        // (a scaffold, a Connect) — a click always re-reads.
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, WireifyAppStatus Status)> _appStatus =
            new(StringComparer.OrdinalIgnoreCase);
        static readonly TimeSpan AppStatusTtl = TimeSpan.FromSeconds(2);

        public WireifyAppStatus AppStatusFor(string? ghFilePath)
        {
            if (string.IsNullOrWhiteSpace(ghFilePath)) return WireifyAppStatus.None;
            var key = ghFilePath!;
            if (_appStatus.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < AppStatusTtl) return hit.Status;
            int port;
            bool listening;
            lock (_gate)
            {
                port = _host?.Port ?? 0;
                listening = _host?.IsListening ?? false;
            }
            var status = AppStatusResolver.Compute(new WireifyPaths(), key, port, listening);
            _appStatus[key] = (DateTime.UtcNow, status);
            return status;
        }

        void ForgetAppStatus() => _appStatus.Clear();

        public string? AppLinkFor(string? ghFilePath)
        {
            var status = AppStatusFor(ghFilePath);
            if (!status.PageExists) return null;
            AppSurface? surface;
            lock (_gate) surface = _appSurface;
            var info = surface?.Describe(status.HomeId);
            return info is { HomeExists: true } ? info.Url : null;
        }

        public WireifyConnectStep OpenApp(string? ghFilePath)
        {
            ForgetAppStatus();
            var status = AppStatusFor(ghFilePath);
            if (!status.PageExists)
                return new WireifyConnectStep("[wireify]", status.Reason, false, "app");
            try
            {
                EnsureServer();
                var url = AppLinkFor(ghFilePath)
                    ?? throw new InvalidOperationException("the app surface holds no link for this home");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                Log("[wireify]", $"app: opened {status.HomeId} in the browser", true);
                return new WireifyConnectStep("[wireify]", "app opened in the browser", true, "app");
            }
            catch (Exception ex)
            {
                Log("[wireify]", $"app: could not open the browser for {status.HomeId}: {ex.Message}", false);
                return new WireifyConnectStep("[wireify]",
                    "could not open the browser — copy the link instead (right-click the socket)", false, "app");
            }
        }

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
                    // The companion-app surface rides the same listener: statics + the app API
                    // per home, gated by per-home per-run tokens (never the MCP secret).
                    var appPaths = new WireifyPaths();
                    var surface = new AppSurface(
                        homeId =>
                        {
                            var dir = Path.Combine(appPaths.ProjectsDir, homeId);
                            return Directory.Exists(dir) ? dir : null;
                        },
                        bridge,
                        (message, ok) => Log("[wireify]", message, ok));
                    _appSurface = surface;
                    var tools = new WireifyTools(bridge, OnToolActivity, surface.Describe,
                        (homeId, template) =>
                        {
                            var dir = Path.Combine(appPaths.ProjectsDir, homeId);
                            if (!Directory.Exists(dir))
                                throw new InvalidOperationException($"no home '{homeId}' on this server.");
                            var report = AppKitScaffolder.Scaffold(ResolveTemplateRoot(), dir, template);
                            // The socket's Open app capsule keys on the page's existence: drop the
                            // cached answer and nudge a repaint so it flips the moment the page exists.
                            ForgetAppStatus();
                            StateChanged?.Invoke(State);
                            var info = surface.Describe(homeId);
                            return new ScaffoldAppResult(
                                info.HomeExists, info.Url, info.AppDirExists, info.ManifestPresent,
                                info.Controls, info.Views,
                                report.Seeded, report.Skipped, KitStamped: true, report.Template,
                                info.ManifestError, info.ManifestWarnings);
                        });
                    _host = new WireifyMcpHost(tools, secret, surface);
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
                // Tokens are per home and never logged wholesale — each home's link prints at
                // its Connect, and get_app_info hands the agent the same URL on ask.
                if (_appSurface is not null)
                    Log("[wireify]", $"webapp surface: /app/<home-id>/ on port {info.Port} — per-home links from the Build line, a socket's Open app, or get_app_info", true);
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
                            "Open a Grasshopper definition, then Build again.");
                    if (string.IsNullOrEmpty(activePath))
                        return Refuse(server.Port, "the definition is unsaved",
                            "Wireify keys the agent home to the .gh file path. Save the definition, then Build again.");
                    path = activePath;
                }

                // Every step this Build raises is kept for the panel, whoever pressed Build.
                using var recording = _steps.Begin(path!);

                WireifyMcpHost host;
                lock (_gate) host = _host!;

                var connector = new WireifyConnector(
                    new WireifyPaths(),
                    new HomeScaffolder(ResolveTemplateRoot()),
                    _launcher);
                // App lines ride the connector's own step list so they land in the console AND
                // the connect-*.log file — the controller-side raise after Connect() returned
                // is why the log always ended at "terminal launched" (round-5 S5.0e, S4.17).
                var result = connector.Connect(path!, host, OnConnectStep, BuildAppLines);

                // Register (or refresh) this definition's session: the document binding routes
                // its tool calls, the per-session state drives ITS sockets only. Registered even
                // when the terminal spawn failed — a manually opened terminal in the home still
                // authenticates with the session header and routes correctly.
                if (!string.IsNullOrEmpty(result.HomeDir))
                {
                    var homeId = Path.GetFileName(result.HomeDir);
                    _sessions.Register(homeId, path!, FindOpenDocumentId(path!), result.Terminal, result.TerminalLaunched);
                    ForgetAppStatus(); // the home exists now — the socket's plate re-reads
                    if (result.Terminal is { } handle)
                    {
                        handle.Exited += () => OnSessionTerminalExited(handle);
                        if (handle.HasExited) OnSessionTerminalExited(handle); // died before the subscription
                    }
                    StateChanged?.Invoke(State);
                }

                var success = result.Steps.All(s => s.Ok);
                var hint = BuildHint(result);
                // The failure hint reaches the panel log, the connect log, and the command line
                // whoever pressed Build: the plate shows its first three segments, and the long
                // form (every platform, the plan requirement) existed on no channel when the
                // socket drove the Build (round-11 S11.31).
                // The socket and the command each echo the hint to the command line already;
                // the Log line is what reaches the panel log and the connect log.
                if (!success && hint is { Length: > 0 })
                    Log("[claude]", hint, false);
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
            _steps.Record(step);
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

        /// <summary>A session's terminal closed. Only the LAST open terminal demotes the session
        /// (its definition's sockets read Build again, the auth transition re-arms) — with another
        /// terminal still open the session stays live and the log says so (round-9 S9.7). Other
        /// definitions' sessions are untouched.</summary>
        void OnSessionTerminalExited(ITerminalHandle handle)
        {
            var exit = _sessions.HandleExit(handle);
            if (exit is null) return;
            StateChanged?.Invoke(State);
            Log("[wireify]", exit.Remaining > 0
                ? $"a Claude terminal closed ({exit.FileName}) — {exit.Remaining} still open for it; the session stays live"
                : $"Claude terminal closed ({exit.FileName}) — Build (or right-click a socket: New Claude session) opens a new one", true);
        }

        public void ActiveDefinitionChanged()
        {
            ForgetAppStatus();
            StateChanged?.Invoke(State);
        }

        public void DefinitionRenamed(string? oldPath, string? newPath)
        {
            ForgetAppStatus();
            var file = _sessions.Repath(oldPath, newPath);
            if (file is null) return;
            StateChanged?.Invoke(State);
            var oldName = Path.GetFileName(oldPath ?? "");
            Log("[wireify]",
                $"definition saved as {file} — its Claude session follows the file; the home and any app page stay with {oldName} (a home is keyed to the .gh path)", true);
        }

        /// <summary>The open document whose file path matches — its id makes the session binding
        /// survive a mid-session SaveAs (the path alone would go stale until the next Connect).</summary>
        /// <summary>The app lines a Connect prints AND logs (they flow through the connector's
        /// step list): the connected home's link first, then every OTHER registered session whose
        /// definition is still open — a Connect on one home must not leave the console blind to
        /// the other homes' running apps (round-5 S5.0e, the S4.17 repeat). Links rotate per run,
        /// so a token in the connect log is dead the moment Rhino restarts — by design. The
        /// closing browser-check line states only what the HOME's settings allow, never a claim
        /// about Chrome itself.</summary>
        IReadOnlyList<string> BuildAppLines(string homeDir)
        {
            AppSurface? surface;
            lock (_gate) surface = _appSurface;
            if (surface is null) return Array.Empty<string>();

            var lines = new List<string>();
            void AddLine(string homeId, string dir)
            {
                var url = surface.Describe(homeId).Url;
                lines.Add(File.Exists(Path.Combine(dir, "app", "index.html"))
                    ? $"app: {url}"
                    : $"app: {url} (no page yet — ask the agent to run scaffold_app)");
            }

            var connectedId = Path.GetFileName(homeDir);
            AddLine(connectedId, homeDir);

            var projectsDir = Path.GetDirectoryName(homeDir);
            if (projectsDir is not null)
            {
                foreach (var (homeId, ghPath) in _sessions.ActiveSessions())
                {
                    if (string.Equals(homeId, connectedId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (FindOpenDocumentId(ghPath) == Guid.Empty) continue; // closed definitions serve nothing
                    AddLine(homeId, Path.Combine(projectsDir, homeId));
                }
            }

            if (HomeAllowsBrowserTools(homeDir))
                lines.Add("browser check: this home's settings allow Claude to verify the app in Chrome (see app/kit/KIT.md)");
            return lines;
        }

        /// <summary>Whether the home's scaffolded settings pre-allow the Claude-in-Chrome
        /// verification toolset. A deterministic read of OUR OWN template output — detecting the
        /// Chrome extension or the client's integration state would be client-internals guessing
        /// (the B1 drift class), so the line this gates only ever describes the settings.</summary>
        static bool HomeAllowsBrowserTools(string homeDir)
        {
            try
            {
                var settings = Path.Combine(homeDir, ".claude", "settings.json");
                return File.Exists(settings)
                    && File.ReadAllText(settings).Contains("mcp__claude-in-chrome__");
            }
            catch { return false; }
        }

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
                return ConnectHints.ClaudeMissing();
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
