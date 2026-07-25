// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using WireifyContract;
using WireifyCore.Hosting;

namespace WireifyCore.Tests;

public class SessionLogWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wireify-tests", Path.GetRandomFileName());

    [Fact]
    public void Appends_lines_to_a_stamped_session_file()
    {
        var writer = new SessionLogWriter(_dir, new DateTime(2026, 7, 6, 14, 30, 5));

        writer.Append(new WireifyLogLine(new DateTime(2026, 7, 6, 14, 30, 6), "[wireify]", "MCP server listening", true));
        writer.Append(new WireifyLogLine(new DateTime(2026, 7, 6, 14, 30, 7), "[wireify]", "set_source failed after 12ms: TimeoutException: wedged", false));

        Assert.EndsWith("session-20260706-143005.log", writer.FilePath);
        var lines = File.ReadAllLines(writer.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("ok ", lines[0]);
        Assert.Contains("MCP server listening", lines[0]);
        Assert.Contains("ERR", lines[1]);
        Assert.Contains("TimeoutException", lines[1]);
    }

    [Fact]
    public void Filesystem_failures_never_throw_notice_once_and_suspend_instead_of_dying()
    {
        // A file where the logs DIRECTORY should be makes CreateDirectory fail on every append.
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory");
        var now = new DateTime(2026, 7, 24, 15, 0, 0);
        var notices = new List<(string Message, bool Ok)>();
        var writer = new SessionLogWriter(_dir, now, (m, ok) => notices.Add((m, ok)), () => now);

        writer.Append(new WireifyLogLine(now, "[wireify]", "first", true));  // fails -> suspend + one notice
        writer.Append(new WireifyLogLine(now, "[wireify]", "second", true)); // inside the window -> skipped, silent

        Assert.False(File.Exists(writer.FilePath));
        var failure = Assert.Single(notices);
        Assert.False(failure.Ok);
        Assert.Contains("session log write failed", failure.Message);
        Assert.Contains("panel log stays live", failure.Message);
    }

    [Fact]
    public void Writer_recovers_after_the_retry_window_and_says_so()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory");
        var now = new DateTime(2026, 7, 24, 15, 0, 0);
        var notices = new List<(string Message, bool Ok)>();
        var writer = new SessionLogWriter(_dir, now, (m, ok) => notices.Add((m, ok)), () => now);

        writer.Append(new WireifyLogLine(now, "[wireify]", "first", true)); // fails -> suspend

        // The blocker clears and the retry window passes: the next append lands and reports recovery.
        File.Delete(_dir);
        now = now.AddSeconds(31);
        writer.Append(new WireifyLogLine(now, "[wireify]", "third", true));

        Assert.True(File.Exists(writer.FilePath));
        Assert.Contains("third", File.ReadAllText(writer.FilePath));
        Assert.Equal(2, notices.Count);
        Assert.True(notices[1].Ok);
        Assert.Contains("session log resumed", notices[1].Message);
    }

    [Fact]
    public void A_persisting_failure_renotices_nothing_and_keeps_retrying_per_window()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory");
        var now = new DateTime(2026, 7, 24, 15, 0, 0);
        var notices = new List<(string Message, bool Ok)>();
        var writer = new SessionLogWriter(_dir, now, (m, ok) => notices.Add((m, ok)), () => now);

        writer.Append(new WireifyLogLine(now, "[wireify]", "first", true)); // fails -> notice
        now = now.AddSeconds(31);
        writer.Append(new WireifyLogLine(now, "[wireify]", "second", true)); // retries, fails again -> no second notice

        Assert.Single(notices);
        Assert.False(File.Exists(writer.FilePath));
    }

    public void Dispose()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "wireify-tests");
            if (File.Exists(_dir)) File.Delete(_dir);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }
}
