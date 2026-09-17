// SPDX-License-Identifier: Apache-2.0
using System.Threading;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Ambient identity of the MCP client whose request is currently being served: the home id
    /// carried in the request's <c>X-Wireify-Home</c> header. Set by the host before it hands the
    /// message to the SDK (the value flows through the request's async context into the tool
    /// invocation); read by <see cref="MarshallingBridge"/>, which snapshots it into the
    /// per-call document-routing slot under its serialization gate. Null = a client without a
    /// session header (a hand-run debug client) — those keep the legacy active-document behavior.
    /// </summary>
    public static class WireifySessionContext
    {
        static readonly AsyncLocal<string?> Home = new();
        static readonly AsyncLocal<bool> PathFirstFlag = new();

        public static string? CurrentHomeId
        {
            get => Home.Value;
            set => Home.Value = value;
        }

        /// <summary>True on the companion-app surface: its URL names a HOME, and a home is a
        /// hash of the .gh path, so the request routes to the open document whose path hashes
        /// to it before any Connect-time binding (the app belongs to the file; the terminal
        /// follows the document instance — after a Save As those differ, round-9 S9.13). The
        /// MCP path keeps binding-first (the instance a terminal was launched for).</summary>
        public static bool PathFirst
        {
            get => PathFirstFlag.Value;
            set => PathFirstFlag.Value = value;
        }

        /// <summary>The per-call snapshot the marshalling seam hands the resolver.</summary>
        public static SessionCallContext? Snapshot()
            => CurrentHomeId is { } home ? new SessionCallContext(home, PathFirst) : null;
    }

    /// <summary>What one serialized bridge call knows about its caller: the home the request
    /// named and whether that surface prefers the path-matched document.</summary>
    public sealed record SessionCallContext(string HomeId, bool PathFirst);
}
