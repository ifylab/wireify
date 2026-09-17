// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace WireifyContract
{
    /// <summary>What a socket knows when it paints — the inputs of its plate copy. Plain data,
    /// filled by the .gha from live state; nothing here is persisted.</summary>
    public sealed class SocketView
    {
        public int Number { get; set; } = 1;
        public string[] InputNames { get; set; } = Array.Empty<string>();
        public bool[] InputWired { get; set; } = Array.Empty<bool>();
        /// <summary>This definition has a launched or connected Claude session.</summary>
        public bool SessionOpen { get; set; }
        /// <summary>The build flow (home, config, preflight, terminal) is running.</summary>
        public bool Building { get; set; }
        public string BuildStep { get; set; } = "";
        /// <summary>The failed step's message after a failed build; empty otherwise.</summary>
        public string BuildError { get; set; } = "";
        public string BuildHint { get; set; } = "";
        public WireifyAppStatus App { get; set; } = WireifyAppStatus.None;
        /// <summary>A short-lived line ("link copied") replacing the first message line.</summary>
        public string Transient { get; set; } = "";
        public bool TransientWarm { get; set; }
    }

    /// <summary>What the socket paints: the two capsule labels (and whether each acts), the
    /// stable app address when a page exists, and the message lines with a warm flag each.</summary>
    public sealed class SocketText
    {
        public SocketText(string buildLabel, bool buildActive, string appLabel, bool appActive,
            string address, string[] lines, bool[] warm)
        {
            BuildLabel = buildLabel ?? "";
            BuildActive = buildActive;
            AppLabel = appLabel ?? "";
            AppActive = appActive;
            Address = address ?? "";
            Lines = lines ?? Array.Empty<string>();
            Warm = warm ?? Array.Empty<bool>();
        }

        public string BuildLabel { get; }
        public bool BuildActive { get; }
        public string AppLabel { get; }
        public bool AppActive { get; }
        public string Address { get; }
        public string[] Lines { get; }
        public bool[] Warm { get; }
    }

    /// <summary>
    /// The socket plate's copy, composed from live state (round-8 §11 + Hossein's design): the
    /// left capsule is the develop gesture (Build opens a Claude terminal; "Session open" is
    /// inert while one is live), the right one the use gesture (Open app, or an inert "no app
    /// yet"), and the lines between say what to do next — the build flow's step or failure,
    /// the prompt to type (derived from the socket's number and wired inputs), or the companion
    /// app's address. There is no "Working" state: a conversion replaces the socket before any
    /// repaint could show one (round-12 S12.14). Pure; the .gha renders it.
    /// </summary>
    public static class SocketCopy
    {
        public const string BuildLabel = "Build";
        public const string SessionOpenLabel = "Session open";
        public const string OpenAppLabel = "Open app";
        public const string NoAppLabel = "no app yet";

        public const string ApproveLine = "first time: approve the wireify server in the terminal";
        public const string AppPageLine = "or: make me an app page for this definition";
        public const string CopyHintLine = "click the address to copy the link";
        public const string IdleLine = "Build opens a Claude terminal for this file.";
        public const string SessionLine = "Claude terminal open for this file.";
        public const string UseAppIdleLine = "Open app to use it. Build to improve it with Claude.";
        public const string UseAppSessionLine = "Open app to use it. Improve it in the open Claude terminal.";
        public const string StartingLine = "starting…";

        /// <summary>A build hint is segmented on this: the plate draws each segment as one line
        /// (up to <see cref="MaxHintLines"/>, host platform first), other surfaces show the
        /// joined form. One long sentence clipped at the plate's width lost the Windows install
        /// line to a macOS curl pipe (round-10 S10.9).</summary>
        public const string HintSeparator = " — ";
        public const int MaxHintLines = 3;

        public static SocketText Compose(SocketView v)
        {
            if (v is null) throw new ArgumentNullException(nameof(v));
            var app = v.App ?? WireifyAppStatus.None;

            var buildLabel = v.SessionOpen ? SessionOpenLabel : BuildLabel;
            var buildActive = !v.Building && !v.SessionOpen;
            var appLabel = app.PageExists ? OpenAppLabel : NoAppLabel;

            var lines = new List<string>();
            var warm = new List<bool>();
            void Add(string line, bool isWarm = false)
            {
                if (string.IsNullOrEmpty(line)) return;
                lines.Add(line);
                warm.Add(isWarm);
            }

            if (v.Building)
            {
                Add(string.IsNullOrEmpty(v.BuildStep) ? StartingLine : v.BuildStep);
                Add(ApproveLine);
            }
            else if (!string.IsNullOrEmpty(v.BuildError))
            {
                Add(v.BuildError, true);
                foreach (var part in HintLines(v.BuildHint))
                    Add(part, true);
            }
            else if (app.PageExists)
            {
                Add(CopyHintLine);
                Add(v.SessionOpen ? UseAppSessionLine : UseAppIdleLine);
            }
            else if (v.SessionOpen)
            {
                Add(SessionLine);
                Add("In it: " + DoLine(v));
                Add(AppPageLine);
            }
            else
            {
                Add(IdleLine);
                Add("Then type: " + DoLine(v));
                Add(AppPageLine);
            }

            if (!string.IsNullOrEmpty(v.Transient))
            {
                if (lines.Count == 0) { lines.Add(""); warm.Add(false); }
                lines[0] = v.Transient;
                warm[0] = v.TransientWarm;
            }

            return new SocketText(buildLabel, buildActive, appLabel, app.PageExists,
                app.PageExists ? app.Address : "", lines.ToArray(), warm.ToArray());
        }

        /// <summary>The prompt hint for this socket: its number plus the wired inputs by name, or
        /// the wire-first nudge when nothing is wired. Generic in1/in2 names are not nudged here:
        /// "do #n" works against them, the agent suggests a rename when it matters, and the
        /// rename-first variant was the one line that trailed off in "…" and read as truncated
        /// (round-9 S9.4/S9.5).</summary>
        public static string DoLine(SocketView v)
        {
            if (v is null) throw new ArgumentNullException(nameof(v));
            var names = v.InputNames ?? Array.Empty<string>();
            var wiredFlags = v.InputWired ?? Array.Empty<bool>();
            var wired = new List<string>();
            for (var i = 0; i < names.Length; i++)
                if (i < wiredFlags.Length && wiredFlags[i]) wired.Add((names[i] ?? "").Trim());

            if (wired.Count == 0) return $"do #{v.Number}: <what to make from the inputs you wire in>";
            return $"do #{v.Number}: <what to compute from {Join(wired)}>";
        }

        static string Join(List<string> names)
            => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + ", …";

        /// <summary>The plate's lines for a build hint: the first <see cref="MaxHintLines"/>
        /// segments, trimmed; an unsegmented hint is one line.</summary>
        public static string[] HintLines(string? hint)
        {
            if (string.IsNullOrEmpty(hint)) return Array.Empty<string>();
            return hint!.Split(new[] { HintSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Take(MaxHintLines)
                .ToArray();
        }
    }
}
