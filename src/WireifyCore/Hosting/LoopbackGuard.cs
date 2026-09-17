// SPDX-License-Identifier: Apache-2.0
using System;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// Pure request-header checks shared by the MCP endpoint and the app surface. Loopback binding
    /// alone is not the DNS-rebinding defense: a rebound request still reaches 127.0.0.1, carrying
    /// the attacker's Host (always) and Origin (usually) — so both surfaces refuse anything that is
    /// not literally this server. The per-run secrets remain the primary control; these checks are
    /// the transport spec's MUSTs and the belt. Pure and unit-tested directly.
    /// </summary>
    public static class LoopbackGuard
    {
        /// <summary>Origin is optional — same-origin requests may omit it entirely. When a browser
        /// does send one, it must be exactly this server.</summary>
        public static bool IsAllowedOrigin(string? origin, int port)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            return string.Equals(origin, $"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(origin, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Host is required (every current client sends it) and must be exactly this
        /// server — the one header a DNS-rebound request cannot avoid carrying wrong.</summary>
        public static bool IsAllowedHost(string? host, int port)
        {
            if (string.IsNullOrEmpty(host)) return false;
            return string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
