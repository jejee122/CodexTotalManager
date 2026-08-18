# Third-Party Notices

CodexTotalManager is licensed under the MIT License. The following third-party
projects remain subject to their own licenses and notices.

## OpenCodex

- Repository: https://github.com/lidge-jun/opencodex
- Local migration baseline: v2.7.41 (`ac73f189cf7e3f4ee55690ed8dc7e354b7e6ed10`)
- Latest release reviewed for compatibility: v2.25.0 (2026-08-18)
- Integration: selected behavior was rewritten in C# under
  `CodexOpenCodexNative`; the npm package is not downloaded or executed.
- Local changes: routing isolation, bounded continuation storage, upstream
  credential boundaries, tool-call bridging, and Windows-specific integration.
- License: MIT. A copy is included in `Resources/OpenCodex/LICENSE`.

## CLIProxyAPI

- Repository: https://github.com/router-for-me/CLIProxyAPI
- Integrated version: v7.2.135
- Integration: an official Windows x64 release artifact is supplied only at
  build time and is locked by SHA-256. The executable is not committed to the
  source repository.
- Official archive SHA-256:
  `80eef3e63e229405362c0f302abba50909cd53f10f6036c438d3f4f765144d34`
- Integrated executable SHA-256:
  `0a8ffc52dfb2a466baa1b006341b350bdb1f76fc70b6cc80375bb99afdff697b`
- License: MIT. A copy is included in `Resources/CLIProxyAPI/LICENSE`.

## Codex Dream Skin

- Repository: https://github.com/Fei-Away/Codex-Dream-Skin
- Integrated version: v1.5.14
- Integration: selected Windows runtime files are embedded as source assets.
- Local changes: isolated test paths, managed-path checks, safe local runtime
  staging, and Total Manager UI integration are retained on top of upstream.
- License: MIT. A copy is included in `Resources/CodexDreamSkin/LICENSE`.

## Node.js

- Website and source: https://nodejs.org/ and https://github.com/nodejs/node
- Release used by the current candidate build: v24.19.0 (Windows x64)
- Integration: the official OpenJS-signed executable and its license are
  supplied at build time. Exact file hashes are recorded in each candidate
  package manifest.
- License: Node.js license; the release package includes `NODE-LICENSE.txt`.

## NuGet libraries

- `System.Security.Cryptography.ProtectedData` 10.0.11 — MIT
- `ZstdSharp.Port` 0.8.8 — MIT
- `Tomlyn` 0.20.0 — BSD-2-Clause

No entry in this notice grants permission to remove an upstream copyright,
license, or attribution file from a redistributed package.
