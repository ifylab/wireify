// SPDX-License-Identifier: Apache-2.0
using System.Drawing;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using WireifyContract;

namespace WireifyGh
{
    /// <summary>
    /// Draws the Wireify number badge over CONVERTED components (any component whose nickname
    /// parses as W&lt;n&gt;) at canvas paint time — the socket's badge, continued after the swap.
    /// Pure display: nothing is written to the document, files stay stock, and on machines
    /// without Wireify the badge simply is not drawn. Sockets are skipped (their attributes
    /// draw their own badge).
    /// </summary>
    internal static class WireifyBadgeOverlay
    {
        const float BadgeWidth = 28f;
        const float BadgeHeight = 14f;
        const float MinZoom = 0.4f;

        static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            Instances.CanvasCreated += Hook;
            if (Instances.ActiveCanvas is { } existing) Hook(existing);
        }

        static void Hook(GH_Canvas canvas)
        {
            canvas.CanvasPostPaintObjects -= Paint; // idempotent across CanvasCreated firings
            canvas.CanvasPostPaintObjects += Paint;
        }

        static void Paint(GH_Canvas canvas)
        {
            var doc = canvas.Document;
            if (doc is null || canvas.Viewport.Zoom < MinZoom) return;

            foreach (var obj in doc.Objects)
            {
                if (obj is not IGH_Component comp) continue;
                if (comp.ComponentGuid == WireifyIds.SocketComponentGuid) continue;
                if (!WireifyIds.TryParseNumber(comp.NickName, out var number)) continue;

                var bounds = comp.Attributes?.Bounds ?? RectangleF.Empty;
                if (bounds.IsEmpty || !canvas.Viewport.IsVisible(ref bounds, 20f)) continue;

                var label = WireifyIds.BadgeLabel(number);
                var width = System.Math.Max(BadgeWidth,
                    GH_FontServer.StringWidth(label, GH_FontServer.Standard) + 12f);
                var rect = new RectangleF(
                    bounds.X + (bounds.Width - width) / 2f,
                    bounds.Y - BadgeHeight - 2f,
                    width,
                    BadgeHeight);

                using var badge = GH_Capsule.CreateTextCapsule(
                    rect, rect, GH_Palette.Blue, label, 2, 0);
                badge.Render(canvas.Graphics, false, comp.Locked, false);
            }
        }
    }
}
