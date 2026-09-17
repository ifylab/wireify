// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using WireifyContract;

namespace WireifyGh
{
    /// <summary>
    /// Draws the Wireify badges at canvas paint time. Two kinds, one mechanism:
    /// the numbered <c>W&lt;n&gt;</c> capsule over CONVERTED components (nickname-keyed — the
    /// socket's badge, continued after the swap), and a plain "wireify" capsule over every other
    /// object recorded in the document's touched set (<see cref="WireifyTouched"/> in the
    /// document value table — objects Wireify created or whose content it wrote). Pure display:
    /// nothing here writes to the document, files stay stock, and on machines without Wireify no
    /// badge is drawn and the value-table entry is inert core-GH data. Sockets are skipped
    /// (their attributes draw their own badge).
    /// </summary>
    internal static class WireifyBadgeOverlay
    {
        const float BadgeWidth = 28f;
        const float BadgeHeight = 14f;
        const float MinZoom = 0.4f;

        static bool _installed;

        // Touched-set parse cache, per document, invalidated by raw-string compare — the value
        // table read is a dictionary lookup and the compare is cheap, so paint stays light while
        // same-process appends (and undo/redo of the table itself) are picked up immediately.
        static readonly Dictionary<Guid, (string Raw, HashSet<Guid> Set)> _touchedCache = new();

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

        static HashSet<Guid> TouchedSet(GH_Document doc)
        {
            string raw;
            try { raw = doc.ValueTable.GetValue(WireifyTouched.Key, ""); }
            catch { return new HashSet<Guid>(); }
            if (_touchedCache.TryGetValue(doc.DocumentID, out var cached) && cached.Raw == raw)
                return cached.Set;
            var set = WireifyTouched.Parse(raw);
            _touchedCache[doc.DocumentID] = (raw, set);
            return set;
        }

        static void Paint(GH_Canvas canvas)
        {
            var doc = canvas.Document;
            if (doc is null || canvas.Viewport.Zoom < MinZoom) return;

            var touched = TouchedSet(doc);

            foreach (var obj in doc.Objects)
            {
                if (obj is IGH_Component c && c.ComponentGuid == WireifyIds.SocketComponentGuid) continue;

                // Converted components keep the numbered capsule (nickname is the key, as
                // before); everything else in the touched set gets the plain one — including a
                // converted component whose W-nickname was later renamed away.
                string? label = null;
                if (obj is IGH_Component comp && WireifyIds.TryParseNumber(comp.NickName, out var number))
                    label = WireifyIds.BadgeLabel(number);
                else if (touched.Contains(obj.InstanceGuid))
                    label = "wireify";
                if (label is null) continue;

                var bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
                if (bounds.IsEmpty || !canvas.Viewport.IsVisible(ref bounds, 20f)) continue;

                var width = System.Math.Max(BadgeWidth,
                    GH_FontServer.StringWidth(label, GH_FontServer.Standard) + 12f);
                var rect = new RectangleF(
                    bounds.X + (bounds.Width - width) / 2f,
                    bounds.Y - BadgeHeight - 2f,
                    width,
                    BadgeHeight);

                using var badge = GH_Capsule.CreateTextCapsule(
                    rect, rect, GH_Palette.Blue, label, 2, 0);
                badge.Render(canvas.Graphics, false, (obj as IGH_ActiveObject)?.Locked ?? false, false);
            }
        }
    }
}
