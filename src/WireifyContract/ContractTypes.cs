// SPDX-License-Identifier: Apache-2.0
using System;

namespace WireifyContract
{
    /// <summary>Session-level connection state, driving the panel dot + the socket button label.</summary>
    public enum WireifyConnectionState
    {
        /// <summary>Plugin loaded, MCP server not started yet.</summary>
        ServerStopped = 0,
        /// <summary>Loopback MCP server listening; no Claude terminal launched yet.</summary>
        ServerListening = 1,
        /// <summary>Terminal spawned; waiting for Claude's first authenticated request
        /// (first run per definition also waits on the user approving the wireify server).</summary>
        TerminalLaunched = 2,
        /// <summary>Our server saw an authenticated MCP request — Claude is connected.</summary>
        Connected = 3,
    }

    public sealed class WireifyServerInfo
    {
        public WireifyServerInfo(int port, string url, bool listening)
        {
            Port = port;
            Url = url ?? "";
            Listening = listening;
        }

        public int Port { get; }
        public string Url { get; }
        public bool Listening { get; }
    }

    /// <summary>One step of the connect flow, scope-tagged (<c>[wireify]</c> vs <c>[claude]</c>)
    /// so a failure is attributed to the right side of the boundary. <see cref="Kind"/> is a stable
    /// key ("server", "home", "config", "preflight", "terminal", "refused", "error") the panel
    /// routes rows by — never match on the human-readable message.</summary>
    public sealed class WireifyConnectStep
    {
        public WireifyConnectStep(string scope, string message, bool ok, string kind = "")
        {
            Scope = scope ?? "";
            Message = message ?? "";
            Ok = ok;
            Kind = kind ?? "";
        }

        public string Scope { get; }
        public string Message { get; }
        public bool Ok { get; }
        public string Kind { get; }
    }

    public sealed class WireifyLogLine
    {
        public WireifyLogLine(DateTime stampLocal, string scope, string message, bool ok)
        {
            StampLocal = stampLocal;
            Scope = scope ?? "";
            Message = message ?? "";
            Ok = ok;
        }

        public DateTime StampLocal { get; }
        public string Scope { get; }
        public string Message { get; }
        public bool Ok { get; }
    }

    public sealed class WireifyConnectReport
    {
        public WireifyConnectReport(
            bool success,
            int port,
            string homeDir,
            string mcpConfigPath,
            bool claudeFound,
            bool terminalLaunched,
            WireifyConnectStep[] steps,
            string? hint)
        {
            Success = success;
            Port = port;
            HomeDir = homeDir ?? "";
            McpConfigPath = mcpConfigPath ?? "";
            ClaudeFound = claudeFound;
            TerminalLaunched = terminalLaunched;
            Steps = steps ?? Array.Empty<WireifyConnectStep>();
            Hint = hint;
        }

        public bool Success { get; }
        public int Port { get; }
        public string HomeDir { get; }
        public string McpConfigPath { get; }
        public bool ClaudeFound { get; }
        public bool TerminalLaunched { get; }
        public WireifyConnectStep[] Steps { get; }
        /// <summary>Plain next-step guidance when something failed (never null on failure).</summary>
        public string? Hint { get; }
    }

    /// <summary>A Wireify-managed component on the active canvas: a staged socket, or a converted
    /// (now stock) Python component still carrying its <c>W&lt;n&gt;</c> nickname.</summary>
    public sealed class WireifyCanvasItem
    {
        public WireifyCanvasItem(Guid id, int number, string nickName, bool converted, string[] inputNames)
        {
            Id = id;
            Number = number;
            NickName = nickName ?? "";
            Converted = converted;
            InputNames = inputNames ?? Array.Empty<string>();
        }

        public Guid Id { get; }
        public int Number { get; }
        public string NickName { get; }
        public bool Converted { get; }
        public string[] InputNames { get; }
    }
}
