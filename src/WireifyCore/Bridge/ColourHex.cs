// SPDX-License-Identifier: Apache-2.0
using System;
using System.Globalization;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Pure web-hex colour parsing/formatting for the colour-swatch control. Pushes arrive as
    /// the strings browser color inputs and CSS produce (#RGB, #RGBA, #RRGGBB, #RRGGBBAA, the
    /// leading # optional, case-insensitive); state reports the canonical lowercase #rrggbb,
    /// with the alpha byte appended only when it is not opaque — so a round trip through the
    /// app never grows an alpha channel the swatch does not use.
    /// </summary>
    public static class ColourHex
    {
        public static bool TryParse(string? text, out byte a, out byte r, out byte g, out byte b)
        {
            a = 255; r = 0; g = 0; b = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var hex = text!.Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal)) hex = hex.Substring(1);

            switch (hex.Length)
            {
                case 3: // #rgb
                    return TryNibblePair(hex[0], out r) & TryNibblePair(hex[1], out g) & TryNibblePair(hex[2], out b);
                case 4: // #rgba
                    return TryNibblePair(hex[0], out r) & TryNibblePair(hex[1], out g)
                         & TryNibblePair(hex[2], out b) & TryNibblePair(hex[3], out a);
                case 6: // #rrggbb
                    return TryByte(hex, 0, out r) & TryByte(hex, 2, out g) & TryByte(hex, 4, out b);
                case 8: // #rrggbbaa
                    return TryByte(hex, 0, out r) & TryByte(hex, 2, out g)
                         & TryByte(hex, 4, out b) & TryByte(hex, 6, out a);
                default:
                    return false;
            }
        }

        public static string Format(byte a, byte r, byte g, byte b)
            => a == 255
                ? string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", r, g, b)
                : string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}{3:x2}", r, g, b, a);

        static bool TryNibblePair(char c, out byte value)
        {
            value = 0;
            if (!TryNibble(c, out var n)) return false;
            value = (byte)(n * 17); // CSS short form: each digit doubles (f -> ff)
            return true;
        }

        static bool TryByte(string hex, int start, out byte value)
        {
            value = 0;
            if (!TryNibble(hex[start], out var hi) || !TryNibble(hex[start + 1], out var lo)) return false;
            value = (byte)((hi << 4) | lo);
            return true;
        }

        static bool TryNibble(char c, out int value)
        {
            if (c >= '0' && c <= '9') { value = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
            if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
            value = 0;
            return false;
        }
    }
}
