// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using WireifyContract;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// Best-effort persistence of the per-call panel log: one file per Rhino session under the
    /// wireify logs dir, so field failures stay diagnosable after Rhino closes (the in-memory
    /// panel buffer dies with the process). Never throws — logging must not break a session.
    /// A filesystem failure (AV scan, sharing violation, synced home dir) suspends writing for
    /// one retry window instead of killing the log for the rest of the session, and says so
    /// once through the notice callback — a silently dead log is useless during the exact
    /// incident it exists for. The notice must never route back into this writer.
    /// </summary>
    public sealed class SessionLogWriter
    {
        static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(30);

        readonly object _gate = new();
        readonly string _file;
        readonly Action<string, bool>? _notice;
        readonly Func<DateTime> _clock;
        DateTime _suspendedUntil = DateTime.MinValue;
        bool _broken;

        public SessionLogWriter(
            string logsDir, DateTime stamp,
            Action<string, bool>? notice = null, Func<DateTime>? clock = null)
        {
            _file = Path.Combine(logsDir, $"session-{stamp:yyyyMMdd-HHmmss}.log");
            _notice = notice;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public string FilePath => _file;

        public void Append(WireifyLogLine line)
        {
            string? notice = null;
            var noticeOk = true;
            lock (_gate)
            {
                var now = _clock();
                if (now < _suspendedUntil) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    File.AppendAllText(_file,
                        $"{line.StampLocal:HH:mm:ss.fff} {line.Scope} {(line.Ok ? "ok " : "ERR")} {line.Message}{Environment.NewLine}");
                    if (_broken)
                    {
                        _broken = false;
                        notice = "session log resumed";
                    }
                }
                catch (Exception ex)
                {
                    _suspendedUntil = now + RetryWindow;
                    if (!_broken)
                    {
                        _broken = true;
                        notice = $"session log write failed ({ex.GetType().Name}: {ex.Message}) — "
                            + $"retrying every {RetryWindow.TotalSeconds:0}s; the panel log stays live";
                        noticeOk = false;
                    }
                }
            }
            if (notice is not null) _notice?.Invoke(notice, noticeOk);
        }
    }
}
