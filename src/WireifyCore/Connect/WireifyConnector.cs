// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WireifyCore.Mcp;

namespace WireifyCore.Connect
{
    /// <summary>One step of the connect flow, scope-tagged so failures attribute correctly.</summary>
    public sealed record ConnectStep(string Scope, string Message, bool Ok, string Kind = "");

    public sealed record ConnectResult(
        int Port,
        string Secret,
        string HomeDir,
        string McpConfigPath,
        PreflightResult Preflight,
        bool TerminalLaunched,
        IReadOnlyList<ConnectStep> Steps,
        ITerminalHandle? Terminal = null);

    /// <summary>
    /// Orchestrates one Connect, the single action the Rhino command / GH node triggers: read the
    /// running host's port + secret, scaffold the per-.gh home, merge the project <c>.mcp.json</c>,
    /// preflight the Claude CLI, and launch a terminal in the home. Ties <see cref="ConfigMerger"/>,
    /// <see cref="HomeScaffolder"/>, and <see cref="WireifyMcpHost"/> together. Every step is recorded
    /// (scope-tagged) for the panel + a connect log; logging never fails a Connect.
    /// </summary>
    public sealed class WireifyConnector
    {
        readonly WireifyPaths _paths;
        readonly HomeScaffolder _scaffolder;
        readonly ITerminalLauncher _launcher;

        public WireifyConnector(WireifyPaths paths, HomeScaffolder scaffolder, ITerminalLauncher launcher)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _scaffolder = scaffolder ?? throw new ArgumentNullException(nameof(scaffolder));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        }

        public ConnectResult Connect(string ghFilePath, WireifyMcpHost host, Action<ConnectStep>? onStep = null)
        {
            if (host is null) throw new ArgumentNullException(nameof(host));

            var steps = new List<ConnectStep>();
            void Step(ConnectStep s)
            {
                steps.Add(s);
                onStep?.Invoke(s);
            }

            var port = host.Port;
            var secret = host.Secret;
            Step(new ConnectStep("[wireify]", $"server listening on 127.0.0.1:{port}", host.IsListening, "server"));

            var homeDir = _paths.HomeFor(ghFilePath);
            _scaffolder.ScaffoldHome(homeDir, new HomeScaffolder.Substitutions(port, secret, FileLabel(ghFilePath)));
            _scaffolder.SeedSharedDefaults(_paths.SharedDefaults);
            Step(new ConnectStep("[wireify]", $"home scaffolded at {homeDir}", true, "home"));

            var mcpPath = Path.Combine(homeDir, ".mcp.json");
            ConfigMerger.MergeProjectMcpJson(mcpPath, port, secret);
            Step(new ConnectStep("[wireify]", $"MCP config merged into {mcpPath}", true, "config"));

            // Without this, the first session ignores the scaffolded allowlist ("workspace has not
            // been trusted") and every read-only tool prompts. The home is ours, so pre-trusting it
            // is honest; user-owned folders are never touched.
            try
            {
                ConfigMerger.EnsureProjectTrust(_paths.ClaudeJson, homeDir);
                Step(new ConnectStep("[wireify]", "home pre-trusted in ~/.claude.json (read-only tools will not prompt)", true, "trust"));
            }
            catch (Exception ex)
            {
                Step(new ConnectStep("[wireify]", $"could not pre-trust the home ({ex.Message}) — Claude will show its trust dialog once", false, "trust"));
            }

            var preflight = Preflight.CheckClaude();
            Step(new ConnectStep(
                preflight.ClaudeFound ? "[wireify]" : "[claude]",
                preflight.ClaudeFound ? $"claude found at {preflight.ClaudePath}" : preflight.Note ?? "claude not found",
                preflight.ClaudeFound, "preflight"));

            var launched = false;
            ITerminalHandle? terminal = null;
            // Session model + effort are enforced at spawn (`claude --model <m> --effort <e>`)
            // because settings-file defaults proved unreliable (Claude Code owns those files and
            // pre-creates some of them). The values stay user-owned: read from the home's seeded
            // wireify.json; deleting a key hands that choice back to the user's own Claude default.
            var model = ReadHomeModel(homeDir);
            var effort = ReadHomeEffort(homeDir);
            try
            {
                terminal = _launcher.Launch(homeDir, model, effort);
                launched = true;
                Step(new ConnectStep("[wireify]",
                    $"terminal launched in home dir (model: {model ?? "user default"}, effort: {effort ?? "user default"})",
                    true, "terminal"));
            }
            catch (Exception ex)
            {
                Step(new ConnectStep("[wireify]", $"terminal launch failed: {ex.Message}", false, "terminal"));
            }

            TryWriteLog(steps);
            return new ConnectResult(port, secret, homeDir, mcpPath, preflight, launched, steps, terminal);
        }

        /// <summary>The home's standing model, from the seeded <c>wireify.json</c> at the home root
        /// (a Wireify-owned file — Claude Code writes <c>.claude/settings.local.json</c> itself, so
        /// that location is not seedable). Null (missing file/key, unparseable, or an unsafe value)
        /// means: pass no flag.</summary>
        public static string? ReadHomeModel(string homeDir)
        {
            var value = ReadHomeString(homeDir, "model");
            return value is not null && SystemTerminalLauncher.IsSafeModel(value) ? value : null;
        }

        /// <summary>The home's standing reasoning effort (low/medium/high/xhigh/max), same file and
        /// same semantics as <see cref="ReadHomeModel"/>.</summary>
        public static string? ReadHomeEffort(string homeDir)
        {
            var value = ReadHomeString(homeDir, "effort");
            return value is not null && SystemTerminalLauncher.IsSafeEffort(value) ? value : null;
        }

        static string? ReadHomeString(string homeDir, string key)
        {
            try
            {
                var path = Path.Combine(homeDir, "wireify.json");
                if (!File.Exists(path)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty(key, out var m)) return null;
                var value = m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
                if (value is null || value.Length == 0) return null;
                // "default" = the user released this choice to their own Claude setting; the key
                // stays in the file so the per-key merge never re-seeds it.
                return string.Equals(value, "default", StringComparison.OrdinalIgnoreCase) ? null : value;
            }
            catch { return null; }
        }

        static string FileLabel(string ghFilePath)
            => string.IsNullOrWhiteSpace(ghFilePath) ? "(unsaved definition)" : Path.GetFileName(ghFilePath);

        void TryWriteLog(IReadOnlyList<ConnectStep> steps)
        {
            try
            {
                Directory.CreateDirectory(_paths.LogsDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var file = Path.Combine(_paths.LogsDir, $"connect-{stamp}.log");
                var sb = new StringBuilder();
                foreach (var s in steps)
                    sb.AppendLine($"{s.Scope} {(s.Ok ? "ok " : "ERR")} {s.Message}");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* logging is best-effort; never fail a Connect over it */ }
        }
    }
}
