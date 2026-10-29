// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using WireifyCore.Bridge;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Per-home usage record at <c>~/.ify/wireify/usage/&lt;homeId&gt;.json</c>: wireify call and
    /// mutation counts per tool, plus one row per Claude session. Fed by the marshalling seam's
    /// structured <see cref="CallObservation"/> (never by parsing log text), read back at Connect
    /// for the SessionStart warmup and the panel's memory row. Local only, and it never fails a
    /// call: every write is atomic and every error is swallowed (reported once).
    /// </summary>
    public sealed class UsageLedger
    {
        public const int Version = 1;
        public const int MaxSessions = 20;

        /// <summary>The tools that change the definition — the same set the home's PostToolUse
        /// matcher logs for the lesson gate (<c>settings.json.tmpl</c>).</summary>
        public static readonly IReadOnlyCollection<string> MutationTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "convert_staged", "set_source", "set_io", "set_typed_io", "create_python_component", "wire",
        };

        public sealed class ToolUsage
        {
            public int Calls { get; set; }
            public int Failures { get; set; }
            public long TotalMs { get; set; }
        }

        public sealed class SessionRow
        {
            public DateTime StartUtc { get; set; }
            public DateTime? LastCallUtc { get; set; }
            public DateTime? EndUtc { get; set; }
            public int Calls { get; set; }
            public int Mutations { get; set; }
        }

        public sealed class Record
        {
            public int Version { get; set; } = UsageLedger.Version;
            public string HomeId { get; set; } = "";
            public int Calls { get; set; }
            public int Failures { get; set; }
            public int Mutations { get; set; }
            /// <summary>Every session ever opened; <see cref="Sessions"/> keeps only the newest rows.</summary>
            public int SessionCount { get; set; }
            public DateTime? LastCallUtc { get; set; }
            public Dictionary<string, ToolUsage> Tools { get; set; } = new(StringComparer.Ordinal);
            public List<SessionRow> Sessions { get; set; } = new();
        }

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        readonly string _dir;
        readonly Func<DateTime> _utcNow;
        readonly Action<string>? _onError;
        readonly object _gate = new();
        readonly Dictionary<string, Record> _cache = new(StringComparer.OrdinalIgnoreCase);
        bool _reportedError;

        public UsageLedger(string usageDir, Func<DateTime>? utcNow = null, Action<string>? onError = null)
        {
            if (string.IsNullOrEmpty(usageDir)) throw new ArgumentException("usageDir required", nameof(usageDir));
            _dir = usageDir;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _onError = onError;
        }

        public static string PathFor(string usageDir, string homeId) => Path.Combine(usageDir, homeId + ".json");

        /// <summary>Null when the file is missing or unreadable — a home without usage is simply new.</summary>
        public static Record? Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<Record>(File.ReadAllText(path), JsonOptions);
            }
            catch { return null; }
        }

        /// <summary>Count one finished call against its home. Calls without a session header and
        /// companion-app requests are not Claude-session work and are skipped.</summary>
        public void Observe(CallObservation call)
        {
            if (call is null || string.IsNullOrEmpty(call.HomeId) || call.AppSurface) return;
            Update(call.HomeId!, record =>
            {
                var now = _utcNow();
                var open = OpenRow(record);
                if (open is null)
                {
                    open = new SessionRow { StartUtc = now };
                    record.Sessions.Add(open);
                    record.SessionCount++;
                    if (record.Sessions.Count > MaxSessions)
                        record.Sessions.RemoveRange(0, record.Sessions.Count - MaxSessions);
                }
                var mutation = call.Ok && MutationTools.Contains(call.Tool);
                open.Calls++;
                open.LastCallUtc = now;
                if (mutation) open.Mutations++;

                record.Calls++;
                if (!call.Ok) record.Failures++;
                if (mutation) record.Mutations++;
                record.LastCallUtc = now;
                if (!record.Tools.TryGetValue(call.Tool, out var tool))
                    record.Tools[call.Tool] = tool = new ToolUsage();
                tool.Calls++;
                if (!call.Ok) tool.Failures++;
                tool.TotalMs += call.ElapsedMs;
                return true;
            });
        }

        /// <summary>Close the home's open session row: its last terminal exited, or (where terminal
        /// exit is not tracked, macOS) the next Connect opened a new session.</summary>
        public void EndSession(string homeId)
        {
            if (string.IsNullOrEmpty(homeId)) return;
            Update(homeId, record =>
            {
                var open = OpenRow(record);
                if (open is null) return false;
                open.EndUtc = open.LastCallUtc ?? _utcNow();
                return true;
            });
        }

        static SessionRow? OpenRow(Record record)
            => record.Sessions.Count > 0 && record.Sessions[record.Sessions.Count - 1].EndUtc is null
                ? record.Sessions[record.Sessions.Count - 1]
                : null;

        void Update(string homeId, Func<Record, bool> change)
        {
            try
            {
                lock (_gate)
                {
                    if (!_cache.TryGetValue(homeId, out var record))
                    {
                        record = Read(PathFor(_dir, homeId)) ?? new Record();
                        record.HomeId = homeId;
                        _cache[homeId] = record;
                    }
                    if (!change(record)) return;
                    HomeScaffolder.WriteAllText(PathFor(_dir, homeId), JsonSerializer.Serialize(record, JsonOptions) + "\n");
                }
            }
            catch (Exception ex)
            {
                bool report;
                lock (_gate) { report = !_reportedError; _reportedError = true; }
                if (report) _onError?.Invoke($"usage ledger write failed ({ex.Message}) — calls are unaffected; further failures are not logged");
            }
        }

        /// <summary>The SessionStart warmup (fixed lines, local facts only), or null when there is
        /// no previous session worth naming — the hook then prints nothing.</summary>
        public static string? WarmupText(Record? usage, MemoryLedger.LedgerStats? memory)
        {
            var last = usage?.Sessions.LastOrDefault();
            if (last is null || last.Calls == 0) return null;
            var at = (last.LastCallUtc ?? last.StartUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var lesson = memory?.NewestDate ?? (memory is { Entries: > 0 } ? "undated" : "none yet");
            return string.Format(CultureInfo.InvariantCulture,
                "Previous session on this definition: {0} wireify mutation{1}, last at {2} UTC; last recorded lesson: {3}.\n"
                + "If that session ended mid-task, orient with get_document_summary before resuming.\n",
                last.Mutations, last.Mutations == 1 ? "" : "s", at, lesson);
        }
    }
}
