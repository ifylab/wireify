#!/bin/sh
# Wireify lesson gate: block the session end ONCE when components were built or
# rewired but MEMORY.md gained no lesson. Every check is a local file test;
# fail-open on any missing state.
dir="$(cd "$(dirname "$0")/.." && pwd)"
homedir="$(dirname "$dir")"
input="$(cat)"
case "$input" in
  *'"stop_hook_active":true'*|*'"stop_hook_active": true'*) exit 0 ;;
esac
marker="$dir/session-start-marker"
[ -f "$marker" ] || exit 0
[ -s "$dir/mutations.log" ] || exit 0
[ "$dir/mutations.log" -nt "$marker" ] || exit 0
if [ -f "$dir/review-done" ] && [ "$dir/review-done" -nt "$marker" ]; then exit 0; fi
mem="$homedir/MEMORY.md"
if [ -f "$mem" ] && [ "$mem" -nt "$marker" ]; then exit 0; fi
: > "$dir/review-done"
echo "Wireify: components were built or rewired this session, but MEMORY.md has no new lesson. If a non-obvious fix, gotcha, or user correction emerged, append it now as '### $(date +%Y-%m-%d) [W<n>] <title>' with Symptom / Cause / Fix / Applies-when (rules at the top of MEMORY.md). If nothing durable emerged, say so and finish." >&2
exit 2
