// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Pure budgeting for viewport geometry. Items are taken whole in tree order until the
    /// vertex budget is spent — never half a mesh — and what was skipped is named, so the page
    /// renders the truth ("214 of 512 items") instead of a silently thinned model. Shaper-style
    /// honesty applied to triangles.
    /// </summary>
    public sealed class GeometryBudget
    {
        public const int DefaultMaxVertices = 150_000;
        public const int DefaultMaxItems = 512;
        public const int CurvePointCap = 128;

        readonly int _maxVertices;
        readonly int _maxItems;

        public GeometryBudget(int maxVertices = DefaultMaxVertices, int maxItems = DefaultMaxItems)
        {
            _maxVertices = maxVertices;
            _maxItems = maxItems;
        }

        public int VertexCount { get; private set; }
        public int TakenCount { get; private set; }
        public int SkippedOverBudget { get; private set; }

        /// <summary>Reserve budget for one item of <paramref name="vertices"/> vertices. False =
        /// the item is skipped whole (budget spent or item cap reached).</summary>
        public bool TryTake(int vertices)
        {
            if (vertices < 0) throw new ArgumentOutOfRangeException(nameof(vertices));
            if (TakenCount >= _maxItems || VertexCount + vertices > _maxVertices)
            {
                SkippedOverBudget++;
                return false;
            }
            VertexCount += vertices;
            TakenCount++;
            return true;
        }

        /// <summary>The honest cap warning, or null when nothing was skipped for budget.</summary>
        public string? BudgetWarning(int itemCount) => SkippedOverBudget == 0
            ? null
            : $"geometry capped at {_maxVertices:n0} vertices / {_maxItems:n0} items — "
              + $"{TakenCount} of {itemCount} item(s) rendered; simplify upstream or watch fewer items";

        /// <summary>Split a face's vertex indices into triangles: a triangle passes through, a
        /// quad becomes two (A,B,C + A,C,D) — the browser buffer is triangles-only.</summary>
        public static void AppendFace(List<int> indices, int a, int b, int c, int d, bool isQuad)
        {
            indices.Add(a); indices.Add(b); indices.Add(c);
            if (!isQuad) return;
            indices.Add(a); indices.Add(c); indices.Add(d);
        }
    }
}
