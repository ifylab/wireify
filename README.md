<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/wireify-lockup-dark-512.png">
    <img src="assets/brand/wireify-lockup-512.png" alt="Wireify" width="400">
  </picture>
</p>

# Wireify

Your own Claude Code, live in Grasshopper. One click connects a Claude terminal to your canvas: it reads the data actually flowing through your wires, writes typed Python components, runs them, reads Grasshopper's errors, and fixes them — while you watch.

**Install:** in Rhino 8 on Windows, run `_PackageManager`, search **wireify**, install, restart Rhino. Mac and Rhino 7 are planned. Building from source works too (below).

## How it works

- The plugin hosts a small MCP server inside Grasshopper, on `127.0.0.1:9473` (or the next free port). Loopback only, gated by a per-session secret. It never leaves your machine.
- **Wireify makes no AI calls and needs no account of its own.** It connects *your* Claude Code — your subscription, your data boundaries. The plugin only exposes canvas tools to it.
- Each `.gh` definition gets its own agent home under `~/.ify/wireify/projects/`, scaffolded with Grasshopper skills and a memory that accumulates what worked. Claude starts warm, and gets warmer per definition.

## The workflow

1. Drop a **Wireify** component (the socket) on the canvas. It shows a number badge — `1`.
2. Click **Connect** (or run `_Wireify`). A terminal opens by itself, already running Claude in this definition's home. First time per definition, approve the `wireify` MCP server when Claude asks — one keypress.
3. Wire your inputs into the socket and rename them (`areas`, `pts`, `min_area`, ...). Zoom in for `+`/`-` to add inputs, like Merge.
4. In the terminal: `do #1: keep the points whose area beats min_area; return culled points and a count.`
5. Claude reads the live input data (tree shapes, types, samples), writes the script, and converts the socket **in place** into a normal Python 3 script component — same position, wires kept, outputs solved and ready to wire on.
6. Revise any time: `revise #1: also return the rejected points.` Converted components are edited in place.

The converted component is a stock Rhino component. **Files you save have no Wireify dependency** — colleagues without the plugin open them like any other definition. Only unconverted sockets (a transient authoring state) need Wireify present.

## Requirements

- **Rhino 8, SR18 or newer**, Windows (the Package Manager build is Windows-only today; Mac is planned). Rhino 7 / IronPython 2 support is planned but not built yet.
- **Claude Code** installed and signed in. It needs a **paid plan — Pro, Max, Team, or Enterprise — or a Claude Console (API) account with credits. Free claude.ai accounts cannot run Claude Code**; a free login is redirected to upgrade.

Install Claude Code (the native installer keeps itself updated and puts `claude` on PATH):

| OS | Command |
|---|---|
| macOS | `curl -fsSL https://claude.ai/install.sh \| bash` |
| Windows (PowerShell) | `irm https://claude.ai/install.ps1 \| iex` |
| Windows (winget) | `winget install Anthropic.ClaudeCode` |

Then run `claude` once in any terminal and complete the browser login. Check with `claude auth status` (or `/status` inside a session, which also shows your plan). **Install or update Claude Code before starting Rhino** — Rhino captures PATH at startup, so a fresh install needs a Rhino restart to be seen.

**Model and effort:** Wireify sessions default to Sonnet at high reasoning effort — the loop is tool orchestration, and the faster tier keeps it responsive. The terminal is spawned as `claude --model <m> --effort <e>`, with both values read from `wireify.json` at the home's root. Wireify seeds `{"model": "sonnet", "effort": "high"}` and merges per key: values you edit are never changed, while newly introduced options are added on Connect. Switch either any time with `/model` inside the session (the spawn defaults reapply on the next Connect); edit the file to change one definition's standing choice, or set a value to `"default"` to use your own Claude setting for it (a deleted line comes back on the next Connect — `"default"` is the release switch).

## Build from source

```
dotnet build Wireify.sln
```

- `src/WireifyGh/bin/Debug/net7.0-windows/` — copy the folder contents into your Grasshopper `Components` folder (the `.gha` plus every `.dll`, `.json`, and `home-template/` beside it).
- `src/Wireify/bin/Debug/net7.0/` — install `Wireify.rhp` via `_PlugInManager` from a folder that also contains the same dependency set.

Restart Rhino, open Grasshopper, and watch the Rhino command line for `[wireify] MCP server listening`.

## Troubleshooting

Every connect writes a step log to `~/.ify/wireify/logs/` and streams into the Wireify panel. Lines are scope-tagged: `[wireify]` means our side, `[claude]` means your Claude Code install — the tag tells you where to look.

| Symptom | Side | Fix |
|---|---|---|
| Panel: `claude not found on PATH` | `[claude]` | Install Claude Code (table above), then restart Rhino so it sees the new PATH. |
| Login page appears instead of a session | `[claude]` | Your account is on the free tier — Claude Code needs Pro/Max/Team/Enterprise or Console API credits. |
| Terminal shows `Ignoring N permissions.allow entries ... workspace has not been trusted` | `[wireify]` | Old build or a failed pre-trust step — Connect again from Rhino (it re-seeds the trust), or accept Claude's trust dialog once in that terminal. |
| Every wireify tool call asks for approval | `[wireify]` | Same cause as above — the home's allowlist only applies once the workspace is trusted. Reconnect, or check the panel log for a failed "pre-trusted" step. |
| Terminal asks to approve the `wireify` server | normal (older builds) | Current builds auto-approve the home's own server (`enableAllProjectMcpServers`). If asked anyway, approve once; `claude mcp reset-project-choices` re-prompts. |
| Panel stuck at "Launched - waiting for Claude" | `[claude]` | Look at the terminal: not signed in (`claude auth status`, then `/login`), or a prompt is still waiting. |
| `/mcp` shows `wireify` as failed | both | Check the panel's Server row — the port there must match `.mcp.json` in the home folder ("Open home" button). Slow machine? Start with `MCP_TIMEOUT=60000 claude`. Then `/mcp reconnect wireify`. |
| Claude connects but tools error with "no active Grasshopper document" | `[wireify]` | Bring the definition's window to front — tools bind to the active canvas. |
| Connect refuses: "definition is unsaved" | normal | Save the `.gh` first; the agent home is keyed to the file path. |
| Closed the Claude terminal, want it back | normal | On Windows, Wireify notices the close — the socket button and panel return to **Connect**; click either. Anywhere: right-click any Wireify socket → **Open Claude terminal**. Fresh terminal, same home, memory intact. |
| Rhino crashes when the first Python 3 component loads | Rhino | Known RhinoCode initialisation fragility on some installs, not Wireify-specific. Update to the latest Rhino 8 SR; if it persists, close Rhino and clear the script cache at `%APPDATA%\McNeel\Rhinoceros\8.0\RhinoCode`, then open the ScriptEditor once before using Wireify. Claude checks the runtime before its first create and warns instead of flailing. |
| Anything unclear | both | `claude doctor` checks the install; `claude --debug` (or `/debug` in-session) logs MCP traffic; compare with the panel log. |

## Trust and privacy

Loopback only. Per-session secret. No outbound network calls, no telemetry, no reading of your Claude credentials — the plugin's job ends at launching your own CLI in the right folder.

One deliberate convenience, stated plainly: Connect marks the generated home folder as trusted in `~/.claude.json` (`hasTrustDialogAccepted`) and the home's settings auto-approve **only** the `wireify` server from its own `.mcp.json`. Both apply exclusively to folders Wireify itself scaffolds — never to your project folders — and exist so read-only introspection works without a wall of permission prompts. Delete the key or the setting if you prefer the prompts.

## License

Apache 2.0.
