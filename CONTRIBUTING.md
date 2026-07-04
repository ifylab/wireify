# Contributing to Wireify

Thanks for looking under the hood.

## Build and test

```
dotnet build Wireify.sln
dotnet test Wireify.sln
```

Everything except the thin Rhino entry layers is unit-tested headless — no Rhino install
needed for the test suite. Rhino-coupled behavior is verified on a real Rhino 8 before
releases. `tools/make-package.sh` assembles the Yak package from Release builds.

## Ground rules

- **License:** contributions are accepted under the repository's Apache-2.0 license
  (inbound = outbound). No CLA.
- **Headers:** new source files start with `// SPDX-License-Identifier: Apache-2.0`.
- **RhinoCode surface:** `RhinoCodePluginGH` is unversioned — anything touching it goes
  through the reflection shims in `WireifyCore/Bridge/RhinoCodeInterop.cs`, fails soft
  with a clear message, and gets pinned by a spike before it is trusted.
- **Boundaries:** the MCP server stays loopback-only with a per-session secret; the plugin
  makes no model calls and never reads Claude credentials. Changes that move either line
  will not be merged.
- Keep pure logic (validation, shaping, matching) out of the Rhino-touching classes so it
  stays testable.

## Bugs

Open an issue with your Rhino version (`_SystemInfo`), the `[wireify]` command-line lines,
and the panel log if the problem is in the connect flow.
