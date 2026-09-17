---
name: wireify-frontend
description: "Design and build this definition's companion web app: propose the page's shape from what the canvas holds, declare controls and views in app/manifest.json (views are a conversation, never auto-picked), scaffold from the kit with scaffold_app, iterate on the page files, and verify in the browser. Use for ANY companion-app page work here - building a page, adding controls/views/charts/tables/a 3D viewport, styling or re-theming, deciding static page vs React, or offering the baked HTML report. Routes kit mechanics to app/kit/KIT.md and generic visual craft to the frontend-design skill."
---

# Wireify frontend

You are building the browser half of a live Grasshopper definition. The page is the
user's; the sync is Wireify's. `app/kit/KIT.md` is the mechanical contract (read it
before touching a page — wiring, binding, gestures, statuses, verify flow); this skill
is the design half: what to build, what to ask, and what honest looks like.

## The flow

1. **Read before proposing.** The document summary, the staged sockets, and the
   conversation say what this definition is FOR. Propose the app's shape in words first
   — pages/sections, which inputs become controls, which outputs are worth watching —
   and let the user redirect before any file exists.
2. **Manifest.** Declare exactly what the proposal agreed: `controls` (inputs the page
   pushes) and `views` (outputs it watches). `id` is the component's InstanceGuid; the
   output name goes in `param`; a control may also carry `label` (the human name the
   page shows — the nickname stays beside it) and `help` (one line under it): write
   both for any page someone other than its maker will read. A wrong id is not silent — it arrives as a `warnings`
   entry on every state frame; fix it, never work around it.
3. **Scaffold.** `scaffold_app` (template `panel` for a control surface, `report` for
   the hero-viewport live-report shape). Its receipt reports what it DID — relay the
   `seeded`/`skipped` truth.
4. **Iterate.** Edit `app/index.html` / `theme.css` (user-owned, never re-stamped); the
   user refreshes, or the manifest watcher reshapes live. Check yourself against
   `api/state` while you work.
5. **Verify in the browser** (KIT.md's section) before presenting — and probe the API
   through `node` for anything that is not the rendered page itself (values, frames,
   geometry counts; KIT.md "Probing without a browser"). Verify with the definition's
   Grasshopper tab in FRONT: pushes refuse from a background tab by design, and the
   pill says "not in front" — that is the rule, not a defect. No browser tools? Hand
   over the URL and checklist and say plainly the page is unverified.

## Views are a conversation, never auto-picked

Controls are usually inferable — what already feeds the socket's inputs wants to be
draggable. Views are NOT: the geometry worth watching is often produced inside a
component and wired onward, and only the user knows which of those matters. Read the
wider canvas topology (introspection carries each param's `id` and live wiring) and ASK
which outputs or downstream params to expose. Two outputs named the same? Rename first,
or address the param by its own guid. The templates' outputs empty state teaches the
same rule on the page itself.

## "Drives the model" is measured, not inferred

When a page groups controls into "drives the model" vs "reference", wiring is not
evidence — a slider with recipients can still move nothing the viewer sees (a tested
definition had a wired span slider that moved nothing: the geometry was JSON-driven around
it). Verify by observation:
push once and watch whether any declared view's data actually changes — or label the
grouping as wiring-based. Never promise motion a drag will not deliver.

## Design language

- **The `.ify` kit is the default language** — tokens, grammar, app density, the four
  faces with four jobs, pills-are-not-buttons. KIT.md's design rules are non-negotiable
  on default pages; derive, don't restate (bounds/items/step come from the frame).
- **Honest states are part of the language.** Empty sections say what to do next; error
  and blocked states name the cause and the remedy; never headings over a void, never a
  dead widget for a `missing` kind.
- **Anti-slop:** one type scale, spacing on the kit's rhythm, no gradient-card filler,
  no decoration that encodes nothing, both themes real (check dark AND light). When the
  page needs real visual invention, load the `frontend-design` skill — its craft
  applies WITHIN the kit language on default pages.
- **Re-theme / re-skin / re-shape** (the variant ladder, in KIT.md) only when the user
  asks for a different look — and say which rung you are on.

## Dataviz honesty

The shaper's honesty must reach the pixels. Axes start where they say they start; units
and counts are labeled; no chartjunk. Truncation is declared, never hidden — a view
delivers 5 samples by default while `tree.dataCount` tells the truth, so a real table
or chart declares `"samples": N` in the manifest (clamped at 500) instead of chunk-
packing or binning upstream. When the frame says `samplesTruncated`, the page says
"showing N of M" beside the data. `chart.bars`/`chart.line` ride the tokens; keep marks
on `--ify-accent` and let the data be the interest.

## Static by default; React is an escalation the user asks for

The kit's vanilla modules carry a control surface, tables, charts, and a viewport
without a build step, offline, self-contained — that is the default, and it is not a
lesser tier. Escalate to React only on user ambition the static page genuinely cannot
carry, named in their own asks: client-side routing across many pages, shared
interactive state across components beyond what a page of bindings holds, a component
library they already use, or a build pipeline they already run. The path is a
documented vite scaffold whose build output is still served from `app/` — the sync
contract (`ify-app.js` core, same endpoints) does not change. "It would be cleaner in
React" is not a trigger; "I want X" where X needs it, is.

## The baked report

Offer the report as its own step — "want a shareable record of this state?" — never as
"the app without Wireify". It is a frozen document: explicit export, banner says
static, one theme hard-baked at capture (set the theme first; bake per theme if both
are needed). Independence WITH working compute is a different product (the 0.4
standalone-export story) — route ambitions there honestly instead of overpromising the
bake.

## Facts that shape pages

- Multiline text pushed to a panel behaves exactly like typing it: one item per line on
  multiline panels. Say so when shapes matter downstream.
- Expression sliders: pushes and bounds speak the RAW slider value; `evaluated` carries
  the expression's result. Render `raw = evaluated`, push raw — pushes stay idempotent.
- `docName` arrives with the `.gh` trimmed; add it back if the page wants the filename.
- Value lists: a pushed number is ALWAYS an item index; push the item's name to select
  by name.
- The token never belongs in a committed or shared file; links come from the socket's
  Open app, Build's console line, or `get_app_info`, and the cookie carries the session
  after the first load.
- **Put a check's verdict beside its control.** A clearance, a count or a pass/fail that
  answers a drag sits under the model or next to the slider, never in a section below the
  fold: the drag and its verdict share the screen.
- **A label is a human name plus its unit; the raw name stays visible.** Declare `label`
  and one line of `help` per control in the manifest; the nickname shows beside the label,
  so a Grasshopper user can map a slider back to the canvas.
- **Label the intended unit when the document's unit word differs; never convert a
  number.** A model drawn in metres inside a millimetre document reads in metres on the
  page, and a text view that must stay byte-identical to the canvas keeps the document's
  word.
