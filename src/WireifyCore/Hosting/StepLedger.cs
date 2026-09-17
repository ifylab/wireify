// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Threading;
using WireifyContract;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// The last Build's steps, per definition. The panel's step rows filled only from the live
    /// event, so a panel opened AFTER a Build — or one that never drove it (the socket did) —
    /// showed dashes above a log that listed every completed step, and was least informative
    /// exactly when someone opened it to diagnose a Build (round-10 S10.5). Steps are recorded
    /// while a Build's scope is open on the calling thread (the connector raises them
    /// synchronously); anything raised outside a scope — terminal exits, app opens — is not a
    /// Build step and stays out. Pure — tested without Rhino.
    /// </summary>
    public sealed class StepLedger
    {
        public const int MaxSteps = 64;

        readonly object _gate = new();
        readonly Dictionary<string, List<WireifyConnectStep>> _byPath = new(StringComparer.OrdinalIgnoreCase);
        readonly AsyncLocal<List<WireifyConnectStep>?> _current = new();

        /// <summary>Open a Build's scope for <paramref name="ghFilePath"/>: a fresh list replaces
        /// that definition's previous Build. Dispose the scope when the Build returns.</summary>
        public IDisposable Begin(string ghFilePath)
        {
            if (string.IsNullOrEmpty(ghFilePath)) throw new ArgumentException("a Build needs a definition path", nameof(ghFilePath));
            var list = new List<WireifyConnectStep>();
            lock (_gate) _byPath[ghFilePath] = list;
            _current.Value = list;
            return new Scope(this);
        }

        /// <summary>Record a step against the Build in scope on this thread; a no-op otherwise.</summary>
        public void Record(WireifyConnectStep step)
        {
            if (step is null || _current.Value is not { } list) return;
            lock (_gate)
            {
                if (list.Count < MaxSteps) list.Add(step);
            }
        }

        /// <summary>The last Build's steps for a definition, oldest first; empty when none ran
        /// in this Rhino session.</summary>
        public IReadOnlyList<WireifyConnectStep> For(string? ghFilePath)
        {
            if (string.IsNullOrEmpty(ghFilePath)) return Array.Empty<WireifyConnectStep>();
            lock (_gate)
                return _byPath.TryGetValue(ghFilePath!, out var list) ? list.ToArray() : Array.Empty<WireifyConnectStep>();
        }

        sealed class Scope : IDisposable
        {
            StepLedger? _owner;
            public Scope(StepLedger owner) => _owner = owner;
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null) owner._current.Value = null;
            }
        }
    }
}
