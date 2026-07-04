// SPDX-License-Identifier: Apache-2.0
using System;
using System.Globalization;

namespace WireifyContract
{
    /// <summary>
    /// Stable identifiers + the socket nickname convention, shared by both sides of the ALC
    /// boundary: the GH entry assembly (assigns numbers, draws badges) and the core bridge
    /// (recognises sockets and converted components when it scans the document).
    /// </summary>
    public static class WireifyIds
    {
        /// <summary>ComponentGuid of the Wireify socket component (WireifyGh).</summary>
        public static readonly Guid SocketComponentGuid = new Guid("b1e7c0de-0000-4000-8000-00000000a002");

        /// <summary>Panel id of the Eto Connect/Status panel (Wireify.rhp).</summary>
        public static readonly Guid PanelGuid = new Guid("b1e7c0de-0000-4000-8000-00000000a003");

        /// <summary>Default port — "WIRE" on a phone keypad. Scan-up on collision; the resolved
        /// port is written into the per-home <c>.mcp.json</c>, so client and server always agree.</summary>
        public const int DefaultPort = 9473;

        /// <summary>Nickname convention carrying the Wireify number: <c>W3</c> staged,
        /// <c>W3 cull-panels</c> once converted. The number survives conversion because the
        /// nickname does; the document itself is the registry.</summary>
        public static string MakeNickname(int number, string? slug = null)
        {
            var head = "W" + number.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(slug) ? head : head + " " + slug!.Trim();
        }

        /// <summary>Display text for the number badge (socket and converted alike). Carries the
        /// brand on shared canvases and screenshots — display only, never part of the nickname.</summary>
        public static string BadgeLabel(int number)
            => "Wireify #" + number.ToString(CultureInfo.InvariantCulture);

        /// <summary>Parse the Wireify number out of a nickname (<c>W3</c> / <c>W3 cull-panels</c>).</summary>
        public static bool TryParseNumber(string? nickName, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(nickName)) return false;
            var s = nickName!.Trim();
            if (s.Length < 2 || s[0] != 'W' || !char.IsDigit(s[1])) return false;

            var end = 1;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            if (end < s.Length && s[end] != ' ') return false;

            return int.TryParse(s.Substring(1, end - 1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                && number > 0;
        }
    }
}
