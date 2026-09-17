// SPDX-License-Identifier: Apache-2.0
using System.Linq;
using WireifyContract;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

/// <summary>The CLI-not-found hint, host platform first and plate-sized (round-10 S10.9: a
/// Windows user was handed a macOS curl pipe and the PowerShell line was clipped off).</summary>
public class ConnectHintsTests
{
    [Fact]
    public void Windows_leads_with_powershell_and_mac_with_curl()
    {
        var win = SocketCopy.HintLines(ConnectHints.ClaudeMissing(windows: true));
        var mac = SocketCopy.HintLines(ConnectHints.ClaudeMissing(windows: false));

        Assert.Equal(new[] { ConnectHints.IntroLine, ConnectHints.WindowsLine, ConnectHints.SignInLine }, win);
        Assert.Equal(new[] { ConnectHints.IntroLine, ConnectHints.MacLine, ConnectHints.SignInLine }, mac);
    }

    [Fact]
    public void Every_plate_line_fits_and_the_joined_form_keeps_both_platforms_and_the_plan()
    {
        foreach (var windows in new[] { true, false })
        {
            var joined = ConnectHints.ClaudeMissing(windows);
            Assert.All(SocketCopy.HintLines(joined), line => Assert.True(line.Length <= ConnectHints.PlateLineChars, line));
            Assert.Contains("install.ps1", joined);
            Assert.Contains("install.sh", joined);
            Assert.Contains("Pro/Max/Team/Enterprise", joined);
            Assert.Equal(5, joined.Split(new[] { SocketCopy.HintSeparator }, System.StringSplitOptions.None).Length);
        }
    }
}
