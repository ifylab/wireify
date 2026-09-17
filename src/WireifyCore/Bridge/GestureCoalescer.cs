// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Decides whether an app push starts a new gesture (and so a new undo record) or continues
    /// the current one. Two mechanisms, explicit boundaries first:
    ///
    /// <para><b>Explicit boundaries.</b> A client that knows where a drag begins and ends (the
    /// kit brackets every pointer gesture) sends <c>Open</c>/<c>Close</c> markers; every push
    /// between them belongs to ONE gesture — one undo record — regardless of how slowly the
    /// user's hand moved. Time-gap coalescing cannot distinguish a slow drag from two separate
    /// edits (the round-4 finding: a human drag's ~800 ms steps each became their own record),
    /// so explicit markers are the primary mechanism. A gesture whose Close never arrives (tab
    /// crash, dropped request) expires after the idle timeout: the next push past it starts a
    /// fresh record while the gesture stays open.</para>
    ///
    /// <para><b>Time-gap fallback.</b> Pushes with no open gesture (raw-contract pages that never
    /// send markers) coalesce by quiet gap, measured from the previous push's COMPLETION —
    /// stamped after its solve finished — so a long solve between two pushes of one drag never
    /// counts as user silence. A real pause still splits.</para>
    ///
    /// UI-thread only, like every bridge mutation, so plain dictionaries are safe.
    /// </summary>
    public sealed class GestureCoalescer
    {
        readonly TimeSpan _gap;
        readonly TimeSpan _idle;
        readonly Func<DateTime> _clock;
        readonly Dictionary<Guid, DateTime> _completed = new();
        readonly Dictionary<Guid, OpenGesture> _open = new();

        sealed class OpenGesture
        {
            public DateTime LastActivity;
            public bool Recorded;
        }

        public GestureCoalescer(TimeSpan gap, TimeSpan? idle = null, Func<DateTime>? clock = null)
        {
            _gap = gap;
            _idle = idle ?? TimeSpan.FromSeconds(30);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>An explicit gesture boundary from the client: <c>open: true</c> on
        /// pointer-down, <c>false</c> on pointer-up. Idempotent either way.</summary>
        public void SetGesture(Guid id, bool open)
        {
            if (open)
                _open[id] = new OpenGesture { LastActivity = _clock(), Recorded = false };
            else
                _open.Remove(id);
        }

        /// <summary>True when this push should open a fresh undo record (snapshotting the
        /// pre-gesture state): the first push of an explicitly opened gesture, a push after that
        /// gesture went idle past the timeout, or — with no open gesture — a push past the quiet
        /// gap since this control's last completed push.</summary>
        public bool IsNewGesture(Guid id)
        {
            var now = _clock();
            if (_open.TryGetValue(id, out var gesture))
            {
                var expired = now - gesture.LastActivity > _idle;
                gesture.LastActivity = now;
                if (expired || !gesture.Recorded)
                {
                    gesture.Recorded = true;
                    return true;
                }
                return false;
            }
            return !_completed.TryGetValue(id, out var last) || now - last > _gap;
        }

        /// <summary>Stamp this control's push as completed — call after its solve finishes.</summary>
        public void MarkCompleted(Guid id)
        {
            _completed[id] = _clock();
            if (_open.TryGetValue(id, out var gesture)) gesture.LastActivity = _clock();
        }
    }
}
