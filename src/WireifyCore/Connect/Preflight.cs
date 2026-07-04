// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WireifyCore.Connect
{
    public sealed record PreflightResult(bool ClaudeFound, string? ClaudePath, string? Note);

    /// <summary>
    /// Pre-launch checks that belong to the user's environment, not ours. Right now: is the Claude
    /// Code CLI on PATH? A miss is reported so the connect flow can tag it <c>[claude]</c>-scope and
    /// not pretend to fix something outside our boundary.
    /// </summary>
    public static class Preflight
    {
        public static PreflightResult CheckClaude(string? pathEnv = null, bool? windows = null)
        {
            pathEnv ??= Environment.GetEnvironmentVariable("PATH") ?? "";
            var win = windows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var names = win
                ? new[] { "claude.exe", "claude.cmd", "claude.bat", "claude" }
                : new[] { "claude" };

            foreach (var raw in pathEnv.Split(Path.PathSeparator))
            {
                var dir = raw.Trim();
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var name in names)
                {
                    string candidate;
                    try { candidate = Path.Combine(dir, name); } catch { continue; }
                    if (File.Exists(candidate)) return new PreflightResult(true, candidate, null);
                }
            }

            return new PreflightResult(false, null, "Claude Code CLI not found on PATH — install/auth is on your side.");
        }
    }
}
