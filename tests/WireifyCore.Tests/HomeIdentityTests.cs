// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Text.Json;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class HomeIdentityTests
{
    static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf-identity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    static WireifyPaths PathsAt(string root) => new(root, Path.Combine(root, "claude.json"));

    static string MakeGh(string dir, string name, string content = "gh-bytes")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A pre-existing home with a hand-written home.json (camelCase, as on disk) — the
    /// state an earlier Connect would have left behind.</summary>
    static string MakeHome(WireifyPaths paths, string forGhPath, string? sha, DateTime? orphanedAtUtc = null,
        bool archived = false)
    {
        var homeDir = Path.Combine(archived ? paths.ArchiveDir : paths.ProjectsDir, WireifyPaths.HomeId(forGhPath));
        Directory.CreateDirectory(Path.Combine(homeDir, ".wireify"));
        File.WriteAllText(Path.Combine(homeDir, "MEMORY.md"), "# lessons for " + forGhPath);
        var json = JsonSerializer.Serialize(new
        {
            ghPath = Path.GetFullPath(forGhPath),
            ghSha256 = sha,
            lastConnectUtc = DateTime.UtcNow.AddDays(-1),
            orphanedAtUtc,
        });
        File.WriteAllText(Path.Combine(homeDir, ".wireify", "home.json"), json);
        return homeDir;
    }

    [Fact]
    public void Write_then_read_roundtrips_the_record()
    {
        var root = TempRoot();
        var gh = MakeGh(root, "tower.gh");
        var home = Path.Combine(root, "home");

        HomeIdentity.Write(home, gh);
        var record = HomeIdentity.Read(home);

        Assert.NotNull(record);
        Assert.Equal(Path.GetFullPath(gh), record!.GhPath);
        Assert.Equal(HomeIdentity.TrySha256(gh), record.GhSha256);
        Assert.Equal(64, record.GhSha256!.Length);
        Assert.Null(record.OrphanedAtUtc);
        Assert.True((DateTime.UtcNow - record.LastConnectUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Read_is_null_on_missing_or_malformed_records()
    {
        var root = TempRoot();
        Assert.Null(HomeIdentity.Read(Path.Combine(root, "nothing")));

        var home = Path.Combine(root, "bad");
        Directory.CreateDirectory(Path.Combine(home, ".wireify"));
        File.WriteAllText(Path.Combine(home, ".wireify", "home.json"), "{ not json");
        Assert.Null(HomeIdentity.Read(home));
    }

    [Fact]
    public void TrySha256_hashes_content_not_path()
    {
        var root = TempRoot();
        var a = MakeGh(root, "a.gh", "same-bytes");
        var b = MakeGh(root, "b.gh", "same-bytes");
        var c = MakeGh(root, "c.gh", "other-bytes");

        Assert.Equal(HomeIdentity.TrySha256(a), HomeIdentity.TrySha256(b));
        Assert.NotEqual(HomeIdentity.TrySha256(a), HomeIdentity.TrySha256(c));
        Assert.Null(HomeIdentity.TrySha256(Path.Combine(root, "missing.gh")));
    }

    // ---- adoption ------------------------------------------------------------------------------

    [Fact]
    public void Adopts_on_move_by_exact_content_hash_copying_never_moving()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var oldPath = Path.Combine(root, "old", "tower.gh");
        var oldHome = MakeHome(paths, oldPath, HomeIdentity.TrySha256(MakeGh(root, "seed.gh", "the-bytes")));
        // The file now lives elsewhere, unedited; its old path is gone.
        var newGh = MakeGh(Path.Combine(root, "new"), "tower.gh", "the-bytes");
        var newHome = paths.HomeFor(newGh);

        var outcome = HomeIdentity.TryAdopt(paths, newGh, newHome);

        Assert.True(outcome.Adopted);
        Assert.True(outcome.Ok);
        Assert.StartsWith("adopted memory from", outcome.Note);
        Assert.Contains(Path.GetFileName(oldHome), outcome.Note);
        Assert.Contains("copied", outcome.Note);
        Assert.Empty(outcome.Unresolved);
        Assert.Contains(oldPath, File.ReadAllText(Path.Combine(newHome, "MEMORY.md"))); // lessons traveled

        // Copy semantics: the original is intact (a live terminal may occupy it), marked as
        // adopted so it never matches again, and its archive clock is already running.
        Assert.True(Directory.Exists(oldHome));
        var oldRecord = HomeIdentity.Read(oldHome);
        Assert.Equal(Path.GetFileName(newHome), oldRecord!.AdoptedInto);
        Assert.NotNull(oldRecord.OrphanedAtUtc);
    }

    [Fact]
    public void Adopts_on_rename_in_same_directory_after_edits()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var dir = Path.Combine(root, "proj");
        var oldPath = Path.Combine(dir, "tower-v1.gh");
        var oldHome = MakeHome(paths, oldPath, sha: "0123abcd0123abcd"); // stale hash: edited since
        var newGh = MakeGh(dir, "tower-v2.gh", "edited-bytes");
        var newHome = paths.HomeFor(newGh);

        var outcome = HomeIdentity.TryAdopt(paths, newGh, newHome);

        Assert.True(outcome.Adopted);
        Assert.StartsWith("adopted memory from", outcome.Note);
        Assert.True(Directory.Exists(oldHome)); // copied, not moved
        Assert.True(Directory.Exists(newHome));
        Assert.Equal(Path.GetFileName(newHome), HomeIdentity.Read(oldHome)!.AdoptedInto);
    }

    [Fact]
    public void Adopted_homes_never_match_again()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var dir = Path.Combine(root, "proj");
        var homeA = MakeHome(paths, Path.Combine(dir, "tower-v1.gh"), sha: null);

        // First rename: A adopts into B.
        var ghB = MakeGh(dir, "tower-v2.gh", "bytes-v2");
        var homeB = paths.HomeFor(ghB);
        Assert.True(HomeIdentity.TryAdopt(paths, ghB, homeB).Adopted);
        HomeIdentity.Write(homeB, ghB); // what Connect does after the scaffold

        // Second rename: B's file goes; A (marked) must NOT be a candidate — only B matches.
        File.Delete(ghB);
        var ghC = MakeGh(dir, "tower-v3.gh", "bytes-v3");
        var homeC = paths.HomeFor(ghC);
        var outcome = HomeIdentity.TryAdopt(paths, ghC, homeC);

        Assert.True(outcome.Adopted); // unambiguous despite two on-disk orphan directories
        Assert.Contains(Path.GetFileName(homeB), outcome.Note);
        Assert.Equal(Path.GetFileName(homeC), HomeIdentity.Read(homeB)!.AdoptedInto);
        Assert.Equal(Path.GetFileName(homeB), HomeIdentity.Read(homeA)!.AdoptedInto); // untouched
    }

    [Fact]
    public void Never_adopts_a_copy_whose_original_still_exists()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var original = MakeGh(Path.Combine(root, "proj"), "tower.gh", "the-bytes");
        var originalHome = MakeHome(paths, original, HomeIdentity.TrySha256(original));
        var copy = MakeGh(Path.Combine(root, "proj"), "tower-copy.gh", "the-bytes");
        var copyHome = paths.HomeFor(copy);

        var outcome = HomeIdentity.TryAdopt(paths, copy, copyHome);

        Assert.Null(outcome.Note); // no orphans at all -> nothing to say; a copy scaffolds fresh
        Assert.False(outcome.Adopted);
        Assert.Empty(outcome.Unresolved);
        Assert.True(Directory.Exists(originalHome)); // untouched
        Assert.False(Directory.Exists(copyHome));
    }

    [Fact]
    public void Refuses_on_ambiguity_names_candidates_and_hands_them_off()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var newGh = MakeGh(Path.Combine(root, "proj"), "tower.gh", "same-bytes");
        var sha = HomeIdentity.TrySha256(newGh);
        var orphanA = MakeHome(paths, Path.Combine(root, "a", "one.gh"), sha);
        var orphanB = MakeHome(paths, Path.Combine(root, "b", "two.gh"), sha);
        var newHome = paths.HomeFor(newGh);

        var outcome = HomeIdentity.TryAdopt(paths, newGh, newHome);

        Assert.False(outcome.Adopted);
        Assert.Contains("adoption skipped", outcome.Note);
        Assert.Contains(Path.GetFileName(orphanA), outcome.Note); // candidates named in the line
        Assert.Contains(Path.GetFileName(orphanB), outcome.Note);
        Assert.Equal(2, outcome.Unresolved.Count);
        Assert.All(outcome.Unresolved, c => Assert.True(c.LedgerChars > 0));
        Assert.True(Directory.Exists(orphanA));
        Assert.True(Directory.Exists(orphanB));
        Assert.False(Directory.Exists(newHome));
    }

    [Fact]
    public void No_match_names_the_orphans_and_scaffolds_fresh()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var orphan = MakeHome(paths, Path.Combine(root, "elsewhere", "other.gh"), sha: "feed1234feed1234");
        var newGh = MakeGh(Path.Combine(root, "proj"), "tower.gh", "unrelated-bytes");
        var newHome = paths.HomeFor(newGh);

        var outcome = HomeIdentity.TryAdopt(paths, newGh, newHome);

        Assert.False(outcome.Adopted);
        Assert.Contains("no prior home matched", outcome.Note);
        Assert.Contains("1 orphaned", outcome.Note);
        Assert.Contains(Path.GetFileName(orphan), outcome.Note);
        Assert.Single(outcome.Unresolved);
        Assert.True(Directory.Exists(orphan));
        Assert.False(Directory.Exists(newHome));
    }

    [Fact]
    public void WriteAdoptionCandidates_persists_and_clears()
    {
        var root = TempRoot();
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var file = Path.Combine(home, ".wireify", "adoption-candidates.json");
        var candidates = new[]
        {
            new HomeIdentity.CandidateInfo("tower-a1b2c3d4", @"C:\proj\tower.gh", DateTime.UtcNow, null, 1234),
        };

        HomeIdentity.WriteAdoptionCandidates(home, candidates);
        Assert.True(File.Exists(file));
        var json = File.ReadAllText(file);
        Assert.Contains("tower-a1b2c3d4", json);
        Assert.Contains("ledgerChars", json);
        Assert.Contains("confirm with the user", json);

        // An uneventful later Connect clears the stale handoff.
        HomeIdentity.WriteAdoptionCandidates(home, Array.Empty<HomeIdentity.CandidateInfo>());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Adoption_scan_covers_the_archive()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var archivedHome = MakeHome(paths, Path.Combine(root, "old", "lost.gh"), sha: null,
            orphanedAtUtc: DateTime.UtcNow.AddDays(-200), archived: true);
        // Reopened years later from the same directory it vanished from (rename-in-place tier).
        var newGh = MakeGh(Path.Combine(root, "old"), "lost-restored.gh", "new-bytes");
        var newHome = paths.HomeFor(newGh);

        var outcome = HomeIdentity.TryAdopt(paths, newGh, newHome);

        Assert.True(outcome.Adopted);
        Assert.Contains("archive/", outcome.Note);
        Assert.True(Directory.Exists(archivedHome)); // copied out of the archive, original kept
        Assert.True(Directory.Exists(newHome));
        Assert.Equal(Path.GetFileName(newHome), HomeIdentity.Read(archivedHome)!.AdoptedInto);
    }

    [Fact]
    public void Existing_home_is_never_touched_by_adoption()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var gh = MakeGh(Path.Combine(root, "proj"), "tower.gh");
        var home = paths.HomeFor(gh);
        Directory.CreateDirectory(home);
        MakeHome(paths, Path.Combine(root, "gone", "other.gh"), HomeIdentity.TrySha256(gh)); // tempting orphan

        var outcome = HomeIdentity.TryAdopt(paths, gh, home);
        Assert.Null(outcome.Note);
        Assert.False(outcome.Adopted);
    }

    // ---- homes index ---------------------------------------------------------------------------

    [Fact]
    public void Homes_index_lists_every_home_with_status_and_ledger()
    {
        var root = TempRoot();
        var paths = PathsAt(root);

        var activeGh = MakeGh(Path.Combine(root, "proj"), "alive.gh");
        var active = MakeHome(paths, activeGh, HomeIdentity.TrySha256(activeGh));
        var orphan = MakeHome(paths, Path.Combine(root, "gone", "lost.gh"), sha: null,
            orphanedAtUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var archived = MakeHome(paths, Path.Combine(root, "old", "ancient.gh"), sha: null,
            orphanedAtUtc: DateTime.UtcNow.AddDays(-120), archived: true);
        var legacy = Path.Combine(paths.ProjectsDir, "legacy-home");
        Directory.CreateDirectory(legacy);

        // One more orphan already recovered elsewhere — its record carries the adoption marker.
        var adopted = MakeHome(paths, Path.Combine(root, "moved", "renamed.gh"), sha: null);
        var adoptedRecord = HomeIdentity.Read(adopted)!;
        File.WriteAllText(Path.Combine(adopted, ".wireify", "home.json"), JsonSerializer.Serialize(new
        {
            ghPath = adoptedRecord.GhPath,
            lastConnectUtc = adoptedRecord.LastConnectUtc,
            orphanedAtUtc = DateTime.UtcNow,
            adoptedInto = Path.GetFileName(active),
        }));

        HomeIdentity.WriteHomesIndex(paths, DateTime.UtcNow);

        var index = File.ReadAllText(Path.Combine(root, "homes.md"));
        Assert.Contains("Last regenerated:", index);
        Assert.Contains(Path.GetFileName(active), index);
        Assert.Contains(Path.GetFullPath(activeGh), index); // the definition column carries the path
        Assert.Contains("| active |", index);
        Assert.Contains("orphaned since 2026-07-01", index);
        Assert.Contains(Path.GetFileName(orphan), index);
        Assert.Contains("adopted into `" + Path.GetFileName(active) + "`", index);
        Assert.Contains("archive/" + Path.GetFileName(archived), index);
        Assert.Contains("no identity record", index); // the legacy folder is listed, not hidden
        Assert.Contains("chars", index); // MakeHome seeds a MEMORY.md, so ledger sizes populate
    }

    [Fact]
    public void Homes_index_is_regenerated_whole_every_write()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var home = MakeHome(paths, Path.Combine(root, "gone", "lost.gh"), sha: null);

        HomeIdentity.WriteHomesIndex(paths, DateTime.UtcNow);
        Assert.Contains(Path.GetFileName(home), File.ReadAllText(Path.Combine(root, "homes.md")));

        // The home disappears (archived by hand, cleaned up, whatever): the next regeneration
        // reflects reality — nothing appends, nothing lingers.
        Directory.Delete(home, recursive: true);
        HomeIdentity.WriteHomesIndex(paths, DateTime.UtcNow);

        var index = File.ReadAllText(Path.Combine(root, "homes.md"));
        Assert.DoesNotContain(Path.GetFileName(home), index);
        Assert.Contains("No homes yet", index);
        Assert.Equal(1, index.Split("# Wireify homes").Length - 1); // one header — whole overwrite
    }

    // ---- orphan sweep --------------------------------------------------------------------------

    [Fact]
    public void Sweep_stamps_missing_gh_and_reports_once()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var home = MakeHome(paths, Path.Combine(root, "gone", "lost.gh"), sha: null);

        var note = HomeIdentity.Sweep(paths, currentHomeDir: null, DateTime.UtcNow);
        Assert.NotNull(note);
        Assert.Contains("1 marked orphaned", note);
        Assert.NotNull(HomeIdentity.Read(home)!.OrphanedAtUtc);

        // Second pass: already stamped, nothing new to say.
        Assert.Null(HomeIdentity.Sweep(paths, null, DateTime.UtcNow));
    }

    [Fact]
    public void Sweep_clears_the_stamp_when_the_gh_returns()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var gh = MakeGh(Path.Combine(root, "proj"), "tower.gh");
        var home = MakeHome(paths, gh, sha: null, orphanedAtUtc: DateTime.UtcNow.AddDays(-10));

        var note = HomeIdentity.Sweep(paths, null, DateTime.UtcNow);

        Assert.NotNull(note);
        Assert.Contains("1 restored", note);
        Assert.Null(HomeIdentity.Read(home)!.OrphanedAtUtc);
    }

    [Fact]
    public void Sweep_archives_after_ninety_days_never_deletes()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var home = MakeHome(paths, Path.Combine(root, "gone", "lost.gh"), sha: null,
            orphanedAtUtc: DateTime.UtcNow.AddDays(-91));

        var note = HomeIdentity.Sweep(paths, null, DateTime.UtcNow);

        Assert.NotNull(note);
        Assert.Contains("1 archived", note);
        Assert.False(Directory.Exists(home));
        var archived = Path.Combine(paths.ArchiveDir, Path.GetFileName(home));
        Assert.True(Directory.Exists(archived));
        Assert.True(File.Exists(Path.Combine(archived, "MEMORY.md"))); // moved whole, nothing lost
    }

    [Fact]
    public void Sweep_holds_recent_orphans_out_of_the_archive()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var home = MakeHome(paths, Path.Combine(root, "gone", "lost.gh"), sha: null,
            orphanedAtUtc: DateTime.UtcNow.AddDays(-30));

        Assert.Null(HomeIdentity.Sweep(paths, null, DateTime.UtcNow)); // stamped, not yet 90d: no change
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void Sweep_skips_the_home_just_connected()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        // Its recorded .gh does not exist (a test-style path) — but it IS the current home.
        var current = MakeHome(paths, Path.Combine(root, "nowhere", "current.gh"), sha: null);

        Assert.Null(HomeIdentity.Sweep(paths, current, DateTime.UtcNow));
        Assert.Null(HomeIdentity.Read(current)!.OrphanedAtUtc);
    }

    [Fact]
    public void Sweep_leaves_pre_identity_homes_alone()
    {
        var root = TempRoot();
        var paths = PathsAt(root);
        var legacy = Path.Combine(paths.ProjectsDir, "legacy-home");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "MEMORY.md"), "old lessons");

        Assert.Null(HomeIdentity.Sweep(paths, null, DateTime.UtcNow));
        Assert.True(Directory.Exists(legacy));
    }
}
