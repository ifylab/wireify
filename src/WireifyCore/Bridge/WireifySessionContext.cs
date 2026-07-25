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

        public static string? CurrentHomeId
        {
            get => Home.Value;
            set => Home.Value = value;
        }
    }
}
