<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/wireify-lockup-dark-512.png">
    <img src="assets/brand/wireify-lockup-512.png" alt="Wireify" width="400">
  </picture>
</p>

# Wireify

Your own Claude Code, live in Grasshopper. One click connects a Claude terminal to your canvas: it reads the data actually flowing through your wires, writes typed Python components, runs them, reads Grasshopper's errors, and fixes them — while you watch.

**Install:** in Rhino 8 on Windows, run `_PackageManager`, search **wireify**, install, restart Rhino. Check [Requirements](#requirements) first — Rhino 8 SR18 or newer, and Claude Code on a paid plan. Mac and Rhino 7 are planned. Building from source works too (below).

## How it works

- The plugin hosts a small MCP (Model Context Protocol) server inside Grasshopper, on `127.0.0.1:9473` (or the next free port). Loopback only, gated by a per-session secret. It never leaves your machine. It runs the official MCP C# SDK at the current protocol revision; long-running operations execute as background MCP tasks.
- **Wireify makes no AI calls and needs no account of its own.** It connects *your* Claude Code — your subscription, your data boundaries. The plugin only exposes canvas tools to it.
- Each `.gh` definition gets its own agent home under `~/.ify/wireify/projects/` (that is `%USERPROFILE%\.ify\wireify` on Windows — every `~/.ify/wireify` path in this document lives there), scaffolded with Grasshopper skills and a memory that accumulates what worked. Claude starts warm, and gets warmer per definition.
- **Several definitions open in one Rhino?** Each is its own session: Connect on each file spawns its own terminal (titled with the file's name, carrying that definition's memory), all sharing the one local server. A session's tools are bound to the definition it was connected for — it can read its file even from a background tab, but changes only run while that file is the active canvas, and it never touches your other open files. The socket button is per-definition too: a fresh file reads **Connect** until *it* has a live session.

## The workflow

1. Drop a **Wireify** component (the socket) on the canvas — double-click the canvas and type `wireify` to find it. It shows a number badge — `1` — and takes the nickname `W1`.
2. Click **Connect** (or run `_Wireify`). A terminal opens by itself, already running Claude in this definition's home. First time per definition, approve the `wireify` MCP server when Claude asks — one keypress.
3. Wire your inputs into the socket and rename them (`areas`, `pts`, `min_area`, ...). Zoom in for `+`/`-` to add inputs, like Merge. The built component keeps these names, so rename before converting.
4. In the terminal: `do #1: keep the points whose area beats min_area; return culled points and a count.` The phrasing is free-form — `do #1:` / `revise #1:` are conventions Claude understands, not fixed commands.
5. Claude reads the live input data (tree shapes, types, samples), writes the script, and converts the socket **in place** into a normal Python 3 script component — same position, wires kept, outputs solved and ready to wire on. Expect an approval prompt the first time Claude uses each canvas-changing tool (the convert here) — that is normal Claude Code permissions, not a broken install; read-only introspection never prompts.
6. Revise any time: `revise #1: also return the rejected points.` Converted components are edited in place.

The converted component is a stock Rhino component. **Files you save have no Wireify dependency** — colleagues without the plugin open them like any other definition. Only unconverted sockets (a transient authoring state) need Wireify present.

## Requirements

- **Rhino 8, SR18 (Service Release 18) or newer**, Windows (the Package Manager build is Windows-only today; Mac is planned). Rhino 7 support is planned but not built yet. On Rhino 8, legacy **IronPython 2 / GhPython components are supported today**: Claude reads them, runs them, and ports them to CPython 3 (the `wireify-port` flow); new components default to CPython 3.
- **Claude Code** installed and signed in. It needs a **paid plan — Pro, Max, Team, or Enterprise — or a Claude Console (API) account with credits** ([console.anthropic.com](https://console.anthropic.com)). **Free claude.ai accounts cannot run Claude Code**; a free login is redirected to upgrade.

Install Claude Code (the native installer keeps itself updated and puts `claude` on PATH):

| OS | Command |
|---|---|
| macOS | `curl -fsSL https://claude.ai/install.sh \| bash` |
| Windows (PowerShell) | `irm https://claude.ai/install.ps1 \| iex` |
| Windows (winget) | `winget install Anthropic.ClaudeCode` |

Then run `claude` once in any terminal and complete the browser login. Check with `claude auth status` (or `/status` inside a session, which also shows your plan). **Install or update Claude Code before starting Rhino** — Rhino captures PATH at startup, so a fresh install needs a Rhino restart to be seen.

**Model and effort:** Wireify sessions default to Sonnet 5 at high reasoning effort — the loop is tool orchestration, and the fast frontier tier keeps it responsive. The terminal is spawned as `claude --model <m> --effort <e>`, with both values read from `wireify.json` at the home's root. Wireify seeds `{"model": "sonnet", "effort": "high"}` and merges per key: values you edit are never changed, while newly introduced options are added on Connect. Switch either any time with `/model` inside the session (the spawn defaults reapply on the next Connect); edit the file to change one definition's standing choice, or set a value to `"default"` to use your own Claude setting for it (a deleted line comes back on the next Connect — `"default"` is the release switch).

## Build from source

```
dotnet build Wireify.sln
```

- `src/WireifyGh/bin/Debug/net7.0-windows/` — copy the folder contents into your Grasshopper `Components` folder (the `.gha` plus every `.dll`, `.json`, and `home-template/` beside it).
- `src/Wireify/bin/Debug/net7.0/` — install `Wireify.rhp` via `_PlugInManager` from a folder that also contains the same dependency set.

Restart Rhino, open Grasshopper, and watch the Rhino command line for `[wireify] MCP server listening`.

## Troubleshooting

Every session writes a full log to `~/.ify/wireify/logs/` (`session-*.log` — every tool call, an entry line as it starts and its outcome with timing, plus the real error and stack on failure; `connect-*.log` — the Connect steps) and streams into the Wireify panel. Lines are scope-tagged: `[wireify]` means our side, `[claude]` means your Claude Code install — the tag tells you where to look. If the log file itself can't be written (a sync tool or scanner holding it), the panel says so once and Wireify keeps retrying — the panel log always stays live.

| Symptom | Side | Fix |
|---|---|---|
| Rhino startup: `Error loading - Wireify` / `Unable to load Wireify.rhp plug-in: ID already in use` | install | Two Wireify installs — usually a manual copy in `%APPDATA%\Grasshopper\Libraries\Wireify` next to a Package Manager install under `%APPDATA%\McNeel\Rhinoceros\packages`. Rhino loads one and fails the other, and the failing copy silently never updates. Close Rhino, delete the install you don't want (for the manual copy: remove that `Libraries\Wireify` folder; for the Package Manager copy: `_PackageManager` → Installed → uninstall), restart. The panel log also warns with the exact stale path. |
| Panel: `claude not found on PATH` | `[claude]` | Install Claude Code (table above), then restart Rhino so it sees the new PATH. |
| Login page appears instead of a session | `[claude]` | Your account is on the free tier — Claude Code needs Pro/Max/Team/Enterprise or Console API credits. |
| Terminal shows `Ignoring N permissions.allow entries ... workspace has not been trusted` | `[wireify]` | Old build or a failed pre-trust step — Connect again from Rhino (it re-seeds the trust), or accept Claude's trust dialog once in that terminal. |
| Every wireify tool call asks for approval | `[wireify]` | Same cause as above — the home's allowlist only applies once the workspace is trusted. Reconnect, or check the panel log for a failed "pre-trusted" step. |
| Terminal asks to approve the `wireify` server | normal (older builds) | Current builds auto-approve the home's own server (`enableAllProjectMcpServers`). If asked anyway, approve once; `claude mcp reset-project-choices` re-prompts. |
| Panel stuck at "Launched - waiting for Claude" | `[claude]` | Look at the terminal: not signed in (`claude auth status`, then `/login`), or a prompt is still waiting. |
| `/mcp` shows `wireify` as failed | both | Check the panel's Server row — the port there must match `.mcp.json` in the home folder ("Open home" button). Slow machine? Start with `MCP_TIMEOUT=60000 claude`. Then `/mcp reconnect wireify`. |
| Claude connects but tools error with "no active Grasshopper document" | `[wireify]` | Only hand-run/debug clients (no Wireify session) see this — they read whatever canvas is in front, so open a definition. Wireify-spawned terminals are bound to their own definition and read it even from a background tab. |
| Claude reports `WIREIFY_DOC_NOT_ACTIVE` | normal | That session's definition is open but not the front tab — changes only run on the canvas you are looking at (reads keep working). Click that definition's Grasshopper tab and tell Claude to continue. |
| Claude reports `WIREIFY_DOC_NOT_OPEN` | normal | The definition that session was connected to has been closed in this Rhino. Reopen it, or Connect from the file you mean to work on — every definition gets its own session. |
| Socket shows an orange warning about "exactly 32767 characters" | your data | The text on that wire was clipped upstream — Grasshopper panels truncate pasted text at the Windows textbox limit (32767 chars). The full content never entered the document. Wire the file's path instead and let the generated script read the file. |
| First session in a home may ask to approve an import of `~/.ify/wireify/defaults.md` | normal | Depends on your Claude Code version: on current builds the pre-trusted home imports your shared defaults silently; older builds confirm the external import once per home (it lives outside the project folder). If asked, approve — declining leaves your shared standards unloaded for that home. |
| Connect refuses: "definition is unsaved" | normal | Save the `.gh` first; the agent home is keyed to the file path. |
| Closed the Claude terminal, want it back | normal | On Windows, Wireify notices the close — the socket button and panel return to **Connect**; click either. Anywhere: right-click any Wireify socket → **Open Claude terminal**. Fresh terminal, same home, memory intact. |
| Rhino restarted (or crashed) mid-session | normal | The restart ends the session's local server, so the old Claude terminal can no longer reach Rhino. Re-Connect from Rhino (socket button or `_Wireify`) — a fresh terminal opens in the same home: memory, lessons, and logs persist; the previous conversation does not carry over. |
| Rhino crashes when the first Python 3 component loads | Rhino | Known RhinoCode initialisation fragility on some installs, not Wireify-specific. Update to the latest Rhino 8 SR; if it persists, close Rhino and clear the script cache at `%APPDATA%\McNeel\Rhinoceros\8.0\RhinoCode`, then open the ScriptEditor once before using Wireify. Claude checks the runtime before its first create and warns instead of flailing. |
| Anything unclear | both | `claude doctor` checks the install; `claude --debug` (or `/debug` in-session) logs MCP traffic; compare with the panel log. |

## Dev mode (tester feedback)

For anyone helping test Wireify: say `dev mode on` as the first message of a session, and Claude keeps a structured log of that session's findings — bugs, friction, things that worked — in `~/.ify/wireify/devlog.md`, then stays out of the way. `log: <note>` records a remark verbatim; `dev mode wrap` closes the session with a short summary. It is off unless you ask for it, per session, and the file never leaves your machine — you hand it over yourself if and when you choose.

## Trust and privacy

Loopback only. Per-session secret. No outbound network calls, no telemetry, no reading of your Claude credentials — the plugin's job ends at launching your own CLI in the right folder.

One deliberate convenience, stated plainly: Connect marks the generated home folder as trusted in `~/.claude.json` (`hasTrustDialogAccepted`) and the home's settings auto-approve **only** the `wireify` server from its own `.mcp.json`. Both apply exclusively to folders Wireify itself scaffolds — never to your project folders — and exist so read-only introspection works without a wall of permission prompts. Delete the key or the setting if you prefer the prompts.

Everything Wireify persists stays under `~/.ify/wireify` (`%USERPROFILE%\.ify\wireify`) on your machine. Nothing leaves it:

- `projects/<home>/` — one home per definition: the `MEMORY.md` lesson ledger, its `MEMORY-archive.md` overflow, timestamped `.bak` snapshots taken before any maintenance, and `.wireify/home.json` — the identity record (the definition's path, a content hash, the last Connect time, and orphan/adoption stamps) that lets a renamed or moved `.gh` reconnect to its accumulated memory. When an adoption could not be resolved automatically, `.wireify/adoption-candidates.json` lists the unmatched orphaned homes for a user-confirmed recovery.
- `archive/` — homes whose `.gh` disappeared, aged in after 90 days. Never deleted.
- `homes.md` — a human-readable index of every home's record, regenerated on each Connect. A read-only snapshot, never an authority.
- `defaults.md` — your shared standing conventions, imported into each session (silently on current Claude Code builds, or after the one-time approval above).
- `skills/` — your shared skill folders, copied into each home at Connect.
- `devlog.md` — dev-mode findings, written only after you turn dev mode on in a session.
- `logs/` — the session and Connect logs.

## License

Apache 2.0.
