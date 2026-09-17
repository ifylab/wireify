// SPDX-License-Identifier: Apache-2.0
using System.IO;
using WireifyContract;
using WireifyCore.Connect;

namespace WireifyCore.Hosting
{
    /// <summary>The socket plate's app facts, from the file system alone: a definition's home
    /// is a pure function of its path (<see cref="WireifyPaths.HomeId"/>), so a socket can say
    /// whether an app page exists without any session, and print the address that never rotates
    /// (round-8 §11: the disclosure gap was never an architecture gap).</summary>
    public static class AppStatusResolver
    {
        public const string NoPageReason = "no app page yet";

        public static WireifyAppStatus Compute(WireifyPaths paths, string? ghFilePath, int port, bool listening)
        {
            if (paths is null) throw new System.ArgumentNullException(nameof(paths));
            if (string.IsNullOrWhiteSpace(ghFilePath)) return WireifyAppStatus.None;

            var homeId = WireifyPaths.HomeId(ghFilePath!);
            var homeDir = paths.HomeFor(ghFilePath!);
            var appDir = Path.Combine(homeDir, "app");
            var homeExists = Directory.Exists(homeDir);
            var pageExists = homeExists && File.Exists(Path.Combine(appDir, "index.html"));
            var address = listening && port > 0 ? $"127.0.0.1:{port}/app/{homeId}" : "";
            return new WireifyAppStatus(
                homeExists, pageExists, homeId, address,
                homeExists ? appDir : "",
                pageExists ? "" : NoPageReason);
        }
    }
}
