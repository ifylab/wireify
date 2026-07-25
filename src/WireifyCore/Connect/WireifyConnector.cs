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

            // Identity, part 1 — adoption: a renamed or moved .gh whose old home sits orphaned
            // reconnects to its accumulated memory (COPIED onto the new id BEFORE the scaffold, so
            // the managed refresh lands on the adopted files; the original is marked and ages out
            // via the sweep). Copies-of-files and ambiguity scaffold fresh, with the unresolved
            // candidates handed to the session below; a failure never fails the Connect.
            var adoption = HomeIdentity.AdoptionOutcome.Nothing;
            try
            {
                adoption = HomeIdentity.TryAdopt(_paths, ghFilePath, homeDir);
                if (adoption.Note is not null)
                    Step(new ConnectStep("[wireify]", adoption.Note, adoption.Ok, "home"));
            }
            catch (Exception ex)
            {
                Step(new ConnectStep("[wireify]", $"memory adoption failed ({ex.Message}) — scaffolding fresh", false, "home"));
            }

            var scaffold = _scaffolder.ScaffoldHome(
                homeDir, new HomeScaffolder.Substitutions(port, secret, FileLabel(ghFilePath)), _paths.SharedSkillsDir);
            _scaffolder.SeedSharedDefaults(_paths.SharedDefaults);
            _scaffolder.MergeSharedDefaults(_paths.SharedDefaults);
            Step(new ConnectStep("[wireify]", $"home scaffolded at {homeDir}", true, "home"));
            if (scaffold.MemoryNote is not null)
                Step(new ConnectStep("[wireify]", scaffold.MemoryNote, scaffold.MemoryNoteOk, "home"));
            if (scaffold.MemoryStatus is not null)
                Step(new ConnectStep("[wireify]", scaffold.MemoryStatus, true, "home"));

            // Identity, part 2 — record + sweep: overwrite this home's home.json for the file just
            // connected, then age the OTHER homes (gone .gh -> orphan stamp -> archive/ at 90 days,
            // never delete; a restored .gh clears its stamp), then regenerate the read-only
            // homes.md index from the post-sweep records. One log line only when something
            // changed; never fails a Connect.
            if (!string.IsNullOrWhiteSpace(ghFilePath))
            {
                try
                {
                    HomeIdentity.Write(homeDir, ghFilePath);
                    // A refusal's unresolved orphans persist into the home (an empty list clears
                    // any stale file) — the loop skill offers a user-confirmed recovery from it.
                    HomeIdentity.WriteAdoptionCandidates(homeDir, adoption.Unresolved);
                    if (HomeIdentity.Sweep(_paths, homeDir, DateTime.UtcNow) is { } sweepNote)
                        Step(new ConnectStep("[wireify]", sweepNote, true, "home"));
                    HomeIdentity.WriteHomesIndex(_paths, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    Step(new ConnectStep("[wireify]", $"home identity pass failed ({ex.Message}) — session unaffected", false, "home"));
                }
            }

            var mcpPath = Path.Combine(homeDir, ".mcp.json");
            // The home id rides as the session header: the server routes this client's calls to
            // the definition it was Connected for, active canvas or not.
            ConfigMerger.MergeProjectMcpJson(mcpPath, port, secret, Path.GetFileName(homeDir));
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
                terminal = _launcher.Launch(homeDir, model, effort, "Wireify - " + FileLabel(ghFilePath));
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
