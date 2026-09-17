// SPDX-License-Identifier: Apache-2.0
using System;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// The undo action behind rename_component: ONE action holding the object's name, bounds,
    /// and pivot on both sides of the rename, applied together. The stock pair
    /// (GH_NickNameAction + GH_LayoutAction) restored the name and the pivot but left the
    /// post-rename width in place, and the next layout kept it — Grasshopper sizes a slider to
    /// max(current width, fitted width) — so one ctrl-Z after a long rename pushed the capsule's
    /// right edge out by the whole width delta, across the component it fed (round-10 S10.8,
    /// 120 px over, 61% covered, no tool able to take it back). A snapshot's width IS the fitted
    /// width for its name, so applying the snapshot and laying out reproduces it exactly, right
    /// edge included: the anchor invariant the tool path honours survives undo and redo.
    /// </summary>
    public sealed class RenameLayoutAction : GH_UndoAction
    {
        readonly Guid _id;
        readonly LayoutSnapshot _before;
        readonly LayoutSnapshot _after;

        public RenameLayoutAction(Guid id, LayoutSnapshot before, LayoutSnapshot after)
        {
            _id = id;
            _before = before ?? throw new ArgumentNullException(nameof(before));
            _after = after ?? throw new ArgumentNullException(nameof(after));
        }

        // A rename never changes the solution; the canvas repaints.
        public override bool ExpiresSolution => false;
        public override bool ExpiresDisplay => true;

        protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);

        protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);

        void Apply(GH_Document doc, LayoutSnapshot snapshot)
        {
            var obj = doc?.FindObject(_id, true);
            if (obj is null) return;
            obj.NickName = snapshot.NickName;
            if (obj.Attributes is not { } attrs) return;
            try
            {
                attrs.Pivot = new PointF(snapshot.PivotX, snapshot.PivotY);
                attrs.Bounds = new RectangleF(snapshot.X, snapshot.Y, snapshot.Width, snapshot.Height);
                attrs.ExpireLayout();
                attrs.PerformLayout();
            }
            catch { /* an attribute class that refuses to lay out keeps the name change only */ }
        }

        /// <summary>The object's name and laid-out layout right now.</summary>
        public static LayoutSnapshot Capture(IGH_DocumentObject obj)
        {
            if (obj is null) throw new ArgumentNullException(nameof(obj));
            var attrs = obj.Attributes;
            var bounds = attrs?.Bounds ?? RectangleF.Empty;
            var pivot = attrs?.Pivot ?? PointF.Empty;
            return new LayoutSnapshot(obj.NickName ?? "", bounds.X, bounds.Y, bounds.Width, bounds.Height, pivot.X, pivot.Y);
        }
    }
}
