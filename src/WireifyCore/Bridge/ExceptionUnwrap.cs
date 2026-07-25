// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using System.Reflection;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Unwraps the wrapper exceptions this codebase manufactures on its hot paths — reflection
    /// (<see cref="TargetInvocationException"/>) and awaited tasks (<see cref="AggregateException"/>)
    /// — so error messages name the real failure. The MCP SDK masks every non-McpException into
    /// "An error occurred invoking 'x'."; the tool layer relies on this helper to surface the
    /// innermost cause instead.
    /// </summary>
    public static class ExceptionUnwrap
    {
        public static Exception Innermost(Exception ex)
        {
            var current = ex ?? throw new ArgumentNullException(nameof(ex));
            while (true)
            {
                switch (current)
                {
                    case TargetInvocationException { InnerException: { } inner }:
                        current = inner;
                        continue;
                    case AggregateException agg when agg.InnerExceptions.Count >= 1:
                        current = agg.InnerExceptions[0];
                        continue;
                    default:
                        return current;
                }
            }
        }

        /// <summary>First few stack frames of the innermost exception, flattened to one log line —
        /// enough to name the throw site without flooding the panel log.</summary>
        public static string CompactStack(Exception ex, int frames = 4)
        {
            var stack = Innermost(ex).StackTrace;
            if (string.IsNullOrEmpty(stack)) return "";
            var lines = stack!.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Take(frames);
            return string.Join(" | ", lines);
        }
    }
}
