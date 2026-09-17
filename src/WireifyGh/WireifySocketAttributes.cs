// SPDX-License-Identifier: Apache-2.0
using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using WireifyContract;

namespace WireifyGh
{
    /// <summary>
    /// Socket attributes (the Hairworm capsule technique): the number badge above the component
    /// body, and below it the Wireify plate — the message lines (what to type, the build flow's
    /// steps and errors, the companion app's address) and the two capsules, Build and Open app.
    /// The plate hangs under the body at its own width so the body keeps its stock Grasshopper
    /// look. Everything on the plate is composed at paint time from live state
    /// (<see cref="SocketCopy"/>) — nothing here is written into the definition.
    /// </summary>
    internal sealed class WireifySocketAttributes : GH_ComponentAttributes
    {
        const float BadgeMinWidth = 28f;
        const float BadgeHeight = 14f;
        const float ButtonHeight = 18f;
        const float LineHeight = 13f;
        const float PadX = 7f;
        const float PadY = 5f;
        const float PlateMinWidth = 230f;
        const float PlateMaxWidth = 330f;
        /// <summary>Clear canvas between the body and the plate: Grasshopper draws a variable
        /// parameter component's last "+" straddling the body's bottom edge, and a plate flush
        /// against the body bisected it (round-9 S9.55). The plate reads as its own card.</summary>
        const float PlateGap = 8f;

        static readonly Color TextColour = Color.FromArgb(40, 40, 40);
        static readonly Color MutedColour = Color.FromArgb(105, 105, 105);
        static readonly Color WarmColour = Color.FromArgb(176, 88, 0);

        RectangleF _plateBounds;
        RectangleF _addressBounds;
        RectangleF _buildBounds;
        RectangleF _appBounds;
        SocketText _text = SocketCopy.Compose(new SocketView());

        public WireifySocketAttributes(WireifySocketComponent owner) : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            _text = ((WireifySocketComponent)Owner).Text;

            // The plate widens to its longest line (within limits) and never narrower than the
            // body; the badge floats ABOVE the body outside Bounds, drawn with the exact geometry
            // the converted-component overlay uses (one look everywhere). Drawing outside Bounds
            // is safe — the GH canvas repaints the full viewport per frame (proven live since
            // round 5); picking the overhang is IsPickRegion's job below.
            var body = Bounds;
            var width = PlateMinWidth;
            foreach (var line in _text.Lines)
                width = Math.Max(width, GH_FontServer.StringWidth(line, GH_FontServer.Small) + 2 * PadX);
            width = Math.Max(Math.Min(width, PlateMaxWidth), body.Width);

            var lineCount = (_text.Address.Length > 0 ? 1 : 0) + _text.Lines.Length;
            var plateHeight = PadY + lineCount * LineHeight + PadY + ButtonHeight + PadY;
            _plateBounds = new RectangleF(body.X, body.Bottom + PlateGap, width, plateHeight);

            _addressBounds = _text.Address.Length > 0
                ? new RectangleF(_plateBounds.X + PadX, _plateBounds.Y + PadY, width - 2 * PadX, LineHeight)
                : RectangleF.Empty;

            var row = _plateBounds.Bottom - PadY - ButtonHeight;
            var half = (width - 2 * PadX - 6f) / 2f;
            _buildBounds = new RectangleF(_plateBounds.X + PadX, row, half, ButtonHeight);
            _appBounds = new RectangleF(_plateBounds.X + PadX + half + 6f, row, half, ButtonHeight);

            // Bounds grow DOWN over the plate (gap included) so selection, dragging, and mouse
            // routing cover it.
            Bounds = new RectangleF(body.X, body.Y, body.Width, _plateBounds.Bottom - body.Y);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects) return;

            var owner = (WireifySocketComponent)Owner;

            var badgeLabel = WireifyIds.BadgeLabel(owner.Number);
            var badgeWidth = Math.Max(BadgeMinWidth,
                GH_FontServer.StringWidth(badgeLabel, GH_FontServer.Standard) + 12f);
            var badgeRect = new RectangleF(
                Bounds.X + (Bounds.Width - badgeWidth) / 2f,
                Bounds.Y - BadgeHeight - 2f,
                badgeWidth,
                BadgeHeight);
            using (var badge = GH_Capsule.CreateTextCapsule(
                badgeRect, badgeRect, GH_Palette.Blue, badgeLabel, 2, 0))
            {
                badge.Render(graphics, Selected, Owner.Locked, false);
            }

            using (var plate = GH_Capsule.CreateCapsule(_plateBounds, GH_Palette.White, 3, 0))
            {
                plate.Render(graphics, Selected, Owner.Locked, false);
            }

            var y = _plateBounds.Y + PadY;
            using (var format = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter,
                LineAlignment = StringAlignment.Center,
            })
            {
                if (_text.Address.Length > 0)
                {
                    // The address elides in the MIDDLE of a long home id (path-style), so the
                    // host and the hash suffix — the parts a reader matches on — both survive.
                    using (var addressFormat = new StringFormat(format) { Trimming = StringTrimming.EllipsisPath })
                    using (var brush = new SolidBrush(TextColour))
                    {
                        graphics.DrawString(_text.Address, GH_FontServer.Small, brush, _addressBounds, addressFormat);
                    }
                    y += LineHeight;
                }
                for (var i = 0; i < _text.Lines.Length; i++)
                {
                    var colour = _text.Warm[i] ? WarmColour
                        : i == 0 && _text.Address.Length > 0 ? MutedColour
                        : TextColour;
                    using (var brush = new SolidBrush(colour))
                    {
                        var rect = new RectangleF(_plateBounds.X + PadX, y, _plateBounds.Width - 2 * PadX, LineHeight);
                        graphics.DrawString(_text.Lines[i], GH_FontServer.Small, brush, rect, format);
                    }
                    y += LineHeight;
                }
            }

            var buildPalette = _text.BuildActive ? GH_Palette.Black : GH_Palette.Grey;
            RenderButton(graphics, _buildBounds, _text.BuildLabel, buildPalette);
            RenderButton(graphics, _appBounds, _text.AppLabel, _text.AppActive ? GH_Palette.Black : GH_Palette.Grey);
        }

        void RenderButton(Graphics graphics, RectangleF rect, string label, GH_Palette palette)
        {
            using (var capsule = GH_Capsule.CreateTextCapsule(rect, rect, palette, label, 2, 0))
            {
                capsule.Render(graphics, Selected, Owner.Locked, false);
            }
        }

        public override bool IsPickRegion(PointF point)
            => base.IsPickRegion(point) || _plateBounds.Contains(point);

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var owner = (WireifySocketComponent)Owner;
                if (_buildBounds.Contains(e.CanvasLocation))
                {
                    owner.OnBuildClick();
                    return GH_ObjectResponse.Handled;
                }
                if (_appBounds.Contains(e.CanvasLocation))
                {
                    owner.OnAppClick();
                    return GH_ObjectResponse.Handled;
                }
                if (_addressBounds.Contains(e.CanvasLocation))
                {
                    owner.OnAddressClick();
                    return GH_ObjectResponse.Handled;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override void SetupTooltip(PointF canvasPoint, GH_TooltipDisplayEventArgs e)
        {
            if (_buildBounds.Contains(canvasPoint))
            {
                e.Title = _text.BuildLabel;
                e.Description = _text.BuildActive
                    ? "Opens a Claude terminal in this definition's home. Then type do #n: <what to make> in it."
                    : _text.BuildLabel == SocketCopy.SessionOpenLabel
                        ? "A Claude terminal is open for this file — use it. Right-click for a new session."
                        : "Claude is working on this component.";
                return;
            }
            if (_appBounds.Contains(canvasPoint))
            {
                e.Title = _text.AppLabel;
                e.Description = _text.AppActive
                    ? "Opens this definition's companion app in your browser. The link is minted for this Rhino run; the click is the consent."
                    : "No app page yet. Build, then ask Claude: make me an app page for this definition.";
                return;
            }
            if (_addressBounds.Contains(canvasPoint))
            {
                e.Title = "Companion app";
                e.Description = "Click to copy the working link (it rotates every Rhino run). The address alone never opens the app.";
                return;
            }
            base.SetupTooltip(canvasPoint, e);
        }
    }
}
