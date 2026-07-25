// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class MemoryLedgerTests
{
    const string Header =
        "<!-- wireify:begin — managed -->\n# Wireify memory — f.gh\n\nLedger: 1 / 8,000 chars.\n<!-- wireify:end -->\n" +
        "\n## Lessons\n\n<!-- newest first -->\n";

    static string Entry(string date, string title, string body = "Symptom: s\nCause: c\nFix: f\nApplies-when: a\n")
        => $"### {date} [-] {title}\n{body}";

    [Fact]
    public void Conforming_file_under_budget_is_untouched_and_idempotent()
    {
        var text = Header + Entry("2026-07-06", "two") + Entry("2026-07-05", "one");

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.True(r.Conforming);
        Assert.False(r.Changed);
        Assert.Equal(text, r.Text);
        Assert.Null(r.ArchiveAppend);

        var again = MemoryLedger.Maintain(r.Text, 8000);
        Assert.Equal(r.Text, again.Text);
        Assert.False(again.Changed);
    }

    [Fact]
    public void Legacy_free_text_without_markers_is_not_conforming()
    {
        var text = "# Wireify memory — f.gh\n\nsome old prose lesson\n";

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.False(r.Conforming);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void Legacy_body_below_a_prepended_header_is_not_conforming()
    {
        // The 4.1 legacy transform prepends the managed block above an old seed render; the old
        // H1 + prose below the marker is exactly what the guard must refuse to restructure.
        var text = Header.Replace("\n## Lessons\n\n<!-- newest first -->\n",
            "\n\n# old title\nprose the user wrote\n\n## Lessons\n\n") ;

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.False(r.Conforming);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void User_section_inside_the_lessons_zone_is_not_conforming()
    {
        var text = Header + Entry("2026-07-06", "two") + "## Notes\nuser structure\n";

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.False(r.Conforming);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void Byte_identical_duplicates_collapse_keeping_first()
    {
        var dup = Entry("2026-07-06", "same");
        var other = Entry("2026-07-05", "other");
        var text = Header + dup + other + dup;

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.True(r.Changed);
        Assert.Equal(1, r.DroppedDuplicates);
        Assert.Equal(Header + dup + other, r.Text);
        Assert.Null(r.ArchiveAppend);
    }

    [Fact]
    public void Overflow_archives_oldest_entries_in_file_order()
    {
        var n3 = Entry("2026-07-06", "newest", new string('x', 200) + "\n");
        var n2 = Entry("2026-07-05", "middle", new string('y', 200) + "\n");
        var n1 = Entry("2026-07-04", "oldest", new string('z', 200) + "\n");
        var text = Header + n3 + n2 + n1;
        var budget = Header.Length + n3.Length + 10; // room for the newest only

        var r = MemoryLedger.Maintain(text, budget);

        Assert.True(r.Changed);
        Assert.Equal(2, r.ArchivedEntries);
        Assert.Equal(Header + n3, r.Text);
        Assert.Equal(n2 + n1, r.ArchiveAppend); // file order preserved: newer archived entry first
    }

    [Fact]
    public void A_single_oversized_entry_is_never_archived()
    {
        var huge = Entry("2026-07-06", "huge", new string('x', 9000) + "\n");
        var text = Header + huge;

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.True(r.Conforming);
        Assert.False(r.Changed);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void Hash_lines_inside_code_fences_are_content_not_structure()
    {
        var entry = "### 2026-07-06 [W1] fenced lesson\nSymptom: s\nFix:\n```python\n# a comment\n### not a heading\n```\n";
        var other = Entry("2026-07-05", "other");
        var text = Header + entry + other;

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.True(r.Conforming);
        Assert.False(r.Changed); // two distinct entries, under budget — and only two
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void An_unterminated_code_fence_is_not_conforming()
    {
        var text = Header + "### 2026-07-06 [-] broken\n```python\nnever closed\n";

        var r = MemoryLedger.Maintain(text, 8000);

        Assert.False(r.Conforming);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void Stats_reports_entry_count_and_newest_date()
    {
        var s = MemoryLedger.Stats(Header + Entry("2026-07-09", "newest") + Entry("2026-07-05", "older"));

        Assert.NotNull(s);
        Assert.Equal(2, s!.Entries);
        Assert.Equal("2026-07-09", s.NewestDate); // newest-first is the ledger contract

        var fresh = MemoryLedger.Stats(Header);
        Assert.NotNull(fresh);
        Assert.Equal(0, fresh!.Entries);
        Assert.Null(fresh.NewestDate);
    }

    [Fact]
    public void Stats_is_null_on_unmanaged_text_and_dateless_on_odd_headings()
    {
        Assert.Null(MemoryLedger.Stats("# free text\nno markers here\n"));

        var s = MemoryLedger.Stats(Header + "### undated [W1] title\nSymptom: s\n");
        Assert.NotNull(s);
        Assert.Equal(1, s!.Entries);
        Assert.Null(s.NewestDate);
    }
}
