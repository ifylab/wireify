# Changelog

All notable changes to Wireify are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer.

## [Unreleased]

First working version, live-validated on Rhino 8.29 (Windows):

- In-process MCP server inside Grasshopper (official C# SDK, streamable HTTP, stateless)
  on `127.0.0.1:9473+`, loopback-only, per-session secret.
- The numbered Wireify socket: stage and name inputs on the canvas, then
  `do #1: <task>` in the connected Claude Code terminal.
- `convert_staged`: the socket becomes a stock Python 3 script component in place —
  explicit parameter construction, wires migrated, `W<n>` nickname kept, one undo step.
  Saved definitions carry no Wireify dependency.
- 14-tool surface including live wired-input reading (`read_input_data`), explicit I/O
  definition (`set_io`), source reading (`get_source`), and runtime discovery.
- One-click Connect (socket button, `_Wireify` command, or the panel): scaffolds a
  per-definition agent home with Grasshopper skills and compounding memory, merges the
  MCP config, pre-trusts the generated home, and opens a terminal already running Claude.
- Rhino panel with live connect status and a scope-tagged log; number badge overlay and
  an in-code provenance header on converted components.
