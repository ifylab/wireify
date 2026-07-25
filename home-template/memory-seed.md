<!-- wireify:begin — this header is managed by Wireify and refreshed on every Connect. Your lessons live below the end marker: Wireify never rewrites them; maintenance only removes exact duplicates and moves the oldest entries to MEMORY-archive.md when the file is over budget (a .bak snapshot is written first). -->
# Wireify memory — {{GH_FILE}}

This definition's lesson ledger, imported into context every session. {{MEM_USAGE}}

Recording rules:

- One lesson = one dated entry at the TOP of `## Lessons` (newest first), exactly this shape — the maintenance pass parses the `### ` heading:

  ```
  ### YYYY-MM-DD [W3] short title
  Symptom: what looked broken
  Cause: what was actually wrong
  Fix: what resolved it
  Applies-when: the situation this lesson fires in
  ```

  Use `[-]` when no single component owns the lesson. Keep entries at `### ` level — never `## `.
- A lesson that would hold in a brand-new `.gh` file is a cross-file rule: it belongs in `~/.ify/wireify/defaults.md`, not here.
- At or over the budget ({{MEM_BUDGET}} chars — the usage line above): consolidate BEFORE appending. Merge near-duplicates into one stronger entry, rewrite stale ones, move cross-file rules to defaults.md, then add the new entry. Overflow is archived oldest-first to `MEMORY-archive.md` beside this file — still yours, never deleted.
- DO NOT record: transient errors that passed on retry (UI busy, timeouts), machine-specific state (paths, versions) unless the lesson is about exactly that, "tool X is broken" conclusions from a single occurrence, or one-off task narratives. These harden into false refusals.
<!-- wireify:end -->

## Lessons

<!-- newest first -->
