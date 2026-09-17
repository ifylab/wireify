// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class GeometryBudgetTests
{
    [Fact]
    public void Items_are_taken_whole_until_the_vertex_budget_is_spent()
    {
        var budget = new GeometryBudget(maxVertices: 100, maxItems: 10);

        Assert.True(budget.TryTake(60));
        Assert.False(budget.TryTake(50)); // would cross 100 — skipped whole, never split
        Assert.True(budget.TryTake(40));  // a later smaller item still fits

        Assert.Equal(100, budget.VertexCount);
        Assert.Equal(2, budget.TakenCount);
        Assert.Equal(1, budget.SkippedOverBudget);
    }

    [Fact]
    public void The_item_cap_holds_independently_of_vertices()
    {
        var budget = new GeometryBudget(maxVertices: 1000, maxItems: 2);

        Assert.True(budget.TryTake(1));
        Assert.True(budget.TryTake(1));
        Assert.False(budget.TryTake(1));
    }

    [Fact]
    public void Nothing_skipped_means_no_warning()
    {
        var budget = new GeometryBudget();
        budget.TryTake(10);

        Assert.Null(budget.BudgetWarning(itemCount: 1));
    }

    [Fact]
    public void The_cap_warning_names_the_honest_counts()
    {
        var budget = new GeometryBudget(maxVertices: 10, maxItems: 10);
        budget.TryTake(8);
        budget.TryTake(8);

        var warning = budget.BudgetWarning(itemCount: 2);

        Assert.NotNull(warning);
        Assert.Contains("1 of 2 item(s) rendered", warning);
    }

    [Fact]
    public void Faces_split_quads_into_two_triangles()
    {
        var indices = new List<int>();

        GeometryBudget.AppendFace(indices, 0, 1, 2, 0, isQuad: false);
        GeometryBudget.AppendFace(indices, 4, 5, 6, 7, isQuad: true);

        Assert.Equal(new[] { 0, 1, 2, 4, 5, 6, 4, 6, 7 }, indices);
    }
}
