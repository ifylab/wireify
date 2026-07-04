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

        WireifyMcpHost? _host;
        WireifyConnectionState _state = WireifyConnectionState.ServerStopped;
        bool _sawAuth;
        ITerminalHandle? _terminal; // the CURRENT launch; a superseded handle's exit is ignored

        internal WireifyController(ITerminalLauncher launcher)
            => _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));

        public event Action<WireifyConnectionState>? StateChanged;
        public event Action<WireifyConnectStep>? ConnectStepCompleted;
        public event Action<WireifyLogLine>? LogEmitted;
        public event Action<Guid, bool>? ComponentActivityChanged;

        public WireifyConnectionState State
        {
            get { lock (_gate) return _state; }
        }

        public WireifyServerInfo ServerInfo
        {
            get
            {
                lock (_gate)
                {
                    return _host is { } h
                        ? new WireifyServerInfo(h.Port, $"http://127.0.0.1:{h.Port}/mcp", h.IsListening)
                        : new WireifyServerInfo(0, "", false);
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
                    var secret = Guid.NewGuid().ToString("N"); // per-session; lands in .mcp.json at Connect
                    var bridge = new MarshallingBridge(
                        new GrasshopperBridge(ActiveDocument), _ui,
                        (message, ok) => Log("[wireify]", message, ok));
                    var tools = new WireifyTools(bridge, OnToolActivity);
                    _host = new WireifyMcpHost(tools, secret);
                    _host.AuthenticatedRequest += OnAuthenticatedRequest;
                    _host.Start(WireifyIds.DefaultPort);
                    if (_state == WireifyConnectionState.ServerStopped) _state = WireifyConnectionState.ServerListening;
                    started = true;
                }
                info = new WireifyServerInfo(_host.Port, $"http://127.0.0.1:{_host.Port}/mcp", _host.IsListening);
            }

            if (started)
            {
                StateChanged?.Invoke(WireifyConnectionState.ServerListening);
                Log("[wireify]", $"MCP server listening on {info.Url}", true);
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

                if (result.TerminalLaunched)
                {
                    TrackTerminal(result.Terminal);
                    MarkLaunched();
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
                ConnectStepCompleted?.Invoke(step);
                Log(step.Scope, step.Message, false);
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
        {
            var step = new WireifyConnectStep(s.Scope, s.Message, s.Ok, s.Kind);
            ConnectStepCompleted?.Invoke(step);
            Log(s.Scope, s.Message, s.Ok);
        }

        void OnToolActivity(Guid id, bool active) => ComponentActivityChanged?.Invoke(id, active);

        void Log(string scope, string message, bool ok)
        {
            var line = new WireifyLogLine(DateTime.Now, scope, message, ok);
            lock (_gate)
            {
                _log.Add(line);
                if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
            LogEmitted?.Invoke(line);
        }

        void OnAuthenticatedRequest()
        {
            bool first;
            lock (_gate)
            {
                first = !_sawAuth;
                _sawAuth = true;
                if (first) _state = WireifyConnectionState.Connected;
            }
            if (first)
            {
                StateChanged?.Invoke(WireifyConnectionState.Connected);
                Log("[wireify]", "first authenticated request — Claude connected", true);
            }
        }

        void MarkLaunched()
        {
            bool raise;
            lock (_gate)
            {
                raise = _state == WireifyConnectionState.ServerListening;
                if (raise) _state = WireifyConnectionState.TerminalLaunched;
            }
            if (raise) StateChanged?.Invoke(WireifyConnectionState.TerminalLaunched);
        }

        void TrackTerminal(ITerminalHandle? handle)
        {
            lock (_gate) _terminal = handle;
            if (handle is null) return; // untrackable platform — state keeps its old lifetime rules
            handle.Exited += () => OnTerminalExited(handle);
            if (handle.HasExited) OnTerminalExited(handle); // died before the subscription
        }

        /// <summary>The spawned terminal closed: demote to ServerListening so the socket button and
        /// panel read Connect again, and re-arm the first-authenticated-request transition so the
        /// next terminal can flip the session back to Connected.</summary>
        void OnTerminalExited(ITerminalHandle handle)
        {
            bool demoted;
            lock (_gate)
            {
                if (!ReferenceEquals(_terminal, handle)) return; // an older launch — a newer one owns the state
                _terminal = null;
                demoted = _state >= WireifyConnectionState.TerminalLaunched;
                if (demoted)
                {
                    _state = WireifyConnectionState.ServerListening;
                    _sawAuth = false;
                }
            }
            if (demoted)
            {
                StateChanged?.Invoke(WireifyConnectionState.ServerListening);
                Log("[wireify]", "Claude terminal closed — Connect (or right-click a socket) launches a new one", true);
            }
        }

        WireifyConnectReport Refuse(int port, string reason, string hint)
        {
            var step = new WireifyConnectStep("[wireify]", reason, false, "refused");
            ConnectStepCompleted?.Invoke(step);
            Log(step.Scope, step.Message, false);
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
