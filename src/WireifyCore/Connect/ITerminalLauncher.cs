// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WireifyCore.Connect
{
    /// <summary>Liveness handle for a spawned terminal, so closing the window is visible to the
    /// state machine instead of leaving a dead-green session. Null handle = platform cannot track.</summary>
    public interface ITerminalHandle
    {
        bool HasExited { get; }
        event Action? Exited;
    }

    /// <summary>Opens a fresh terminal in the home dir already running <c>claude</c>. Abstracted so
    /// the connect flow stays testable; the OS-specific implementation is verified live in Rhino.</summary>
    public interface ITerminalLauncher
    {
        /// <summary>Launch <c>claude</c> in <paramref name="homeDir"/>; <paramref name="model"/> and
        /// <paramref name="effort"/> (already validated by the caller) become <c>--model</c> /
        /// <c>--effort</c> flags. Returns a liveness handle, or null when the platform cannot track
        /// the window.</summary>
        ITerminalHandle? Launch(string homeDir, string? model = null, string? effort = null);
    }

    /// <summary>No-op launcher — for tests, or when the caller drives the terminal itself.</summary>
    public sealed class NullTerminalLauncher : ITerminalLauncher
    {
        public ITerminalHandle? Launch(string homeDir, string? model = null, string? effort = null) => null;
    }

    /// <summary>
    /// Spawns the platform terminal running <c>claude</c> in the home dir. Verified live in Rhino,
    /// not in the headless suite (it opens real windows). The Windows path is the primary target and
    /// returns a real liveness handle (closing the window exits the tracked <c>cmd</c>); macOS goes
    /// through <c>osascript</c>, which detaches — no handle there.
    /// </summary>
    public sealed class SystemTerminalLauncher : ITerminalLauncher
    {
        // These values are passed on a command line; anything outside the known alphabets is
        // dropped by the caller, but re-check here since this is the boundary that shells out.
        public static bool IsSafeModel(string model) => Regex.IsMatch(model, @"^[A-Za-z0-9._\[\]-]+$");

        public static bool IsSafeEffort(string effort)
            => effort is "low" or "medium" or "high" or "xhigh" or "max";

        public static string ClaudeCommand(string? model, string? effort = null)
        {
            var command = "claude";
            if (model is { Length: > 0 } m && IsSafeModel(m)) command += $" --model {m}";
            if (effort is { Length: > 0 } e && IsSafeEffort(e)) command += $" --effort {e}";
            return command;
        }

        public ITerminalHandle? Launch(string homeDir, string? model = null, string? effort = null)
        {
            var command = ClaudeCommand(model, effort);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k {command}",
                    WorkingDirectory = homeDir,
                    UseShellExecute = true,
                });
                return process is null ? null : new ProcessTerminalHandle(process);
            }

            var safe = homeDir.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var osa = $"tell application \"Terminal\" to do script \"cd \\\"{safe}\\\" && {command}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{osa.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
            });
            return null; // osascript detaches immediately — its exit says nothing about the window
        }

        sealed class ProcessTerminalHandle : ITerminalHandle
        {
            readonly Process _process;

            public ProcessTerminalHandle(Process process)
            {
                _process = process;
                try
                {
                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) => Exited?.Invoke();
                    if (_process.HasExited) Exited?.Invoke(); // exited before we subscribed
                }
                catch { /* liveness is best-effort; the launch itself already succeeded */ }
            }

            public bool HasExited
            {
                get { try { return _process.HasExited; } catch { return true; } }
            }

            public event Action? Exited;
        }
    }
}
