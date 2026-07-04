// SPDX-License-Identifier: Apache-2.0
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace WireifyGh
{
    /// <summary>
    /// Socket attributes (the Hairworm capsule technique): the number badge above the component
    /// body and a state button below it — "Connect" when the session is down, "Ready - do #n"
    /// once Claude is connected, "Working" while a Wireify tool is touching this component.
    /// </summary>
    internal sealed class WireifySocketAttributes : GH_ComponentAttributes
    {
        const float BadgeMinWidth = 28f;
        const float BadgeHeight = 14f;
        const int ButtonHeight = 20;

        RectangleF _buttonBounds;

        public WireifySocketAttributes(WireifySocketComponent owner) : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();

            // Only the button extends Bounds; the badge floats ABOVE the node, drawn with the exact
            // geometry the converted-component overlay uses (one look everywhere). Drawing slightly
            // outside Bounds is safe — the GH canvas repaints the full viewport per frame (the
            // overlay has proven this live since round 5).
            var body = GH_Convert.ToRectangle(Bounds);
            body.Height += ButtonHeight;
            Bounds = body;

            _buttonBounds = new RectangleF(
                Bounds.X + 2, Bounds.Bottom - ButtonHeight + 2, Bounds.Width - 4, ButtonHeight - 4);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects) return;

            var owner = (WireifySocketComponent)Owner;

            var badgeLabel = WireifyContract.WireifyIds.BadgeLabel(owner.Number);
            var width = System.Math.Max(BadgeMinWidth,
                GH_FontServer.StringWidth(badgeLabel, GH_FontServer.Standard) + 12f);
            var badgeRect = new RectangleF(
                Bounds.X + (Bounds.Width - width) / 2f,
                Bounds.Y - BadgeHeight - 2f,
                width,
                BadgeHeight);
            using (var badge = GH_Capsule.CreateTextCapsule(
                badgeRect, badgeRect, GH_Palette.Blue, badgeLabel, 2, 0))
            {
                badge.Render(graphics, Selected, Owner.Locked, false);
            }

            var label = owner.ButtonLabel;
            var palette = label == "Working" ? GH_Palette.Blue
                : label == "Connect" ? GH_Palette.Black
                : GH_Palette.Grey;
            using (var button = GH_Capsule.CreateTextCapsule(
                _buttonBounds, _buttonBounds, palette, label, 2, 0))
            {
                button.Render(graphics, Selected, Owner.Locked, false);
            }
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left && _buttonBounds.Contains(e.CanvasLocation))
            {
                ((WireifySocketComponent)Owner).OnButtonClick();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(sender, e);
        }
    }
}
