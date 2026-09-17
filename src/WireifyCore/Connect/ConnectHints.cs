// SPDX-License-Identifier: Apache-2.0
using System.Runtime.InteropServices;
using WireifyContract;

namespace WireifyCore.Connect
{
    /// <summary>
    /// The next-step copy a failed Build hands the user. Segmented, host platform first: the
    /// socket plate draws each segment as one non-wrapping line (its width is capped), so a
    /// single long sentence with macOS first showed a Windows user a curl pipe and clipped the
    /// PowerShell line off the plate entirely (round-10 S10.9). The panel row and the command
    /// line keep the joined form, every platform included.
    /// </summary>
    public static class ConnectHints
    {
        /// <summary>Segments a plate line must stay under, in characters — the plate's 330 px
        /// at Grasshopper's small font.</summary>
        public const int PlateLineChars = 56;

        public const string IntroLine = "Claude Code CLI not found. Install it:";
        public const string WindowsLine = "PowerShell: irm https://claude.ai/install.ps1 | iex";
        public const string MacLine = "macOS: curl -fsSL https://claude.ai/install.sh | bash";
        public const string SignInLine = "then run claude once and sign in";
        public const string PlanLine =
            "needs a Pro/Max/Team/Enterprise plan or a Console API account; free claude.ai accounts cannot run Claude Code";

        /// <summary>The CLI-not-found hint for the host platform (overridable for tests):
        /// intro, the host's install line, the sign-in step — the three the plate shows — then
        /// the other platform's line and the plan requirement for the surfaces with room.</summary>
        public static string ClaudeMissing(bool? windows = null)
        {
            var win = windows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            return string.Join(SocketCopy.HintSeparator, new[]
            {
                IntroLine,
                win ? WindowsLine : MacLine,
                SignInLine,
                win ? MacLine : WindowsLine,
                PlanLine,
            });
        }
    }
}
