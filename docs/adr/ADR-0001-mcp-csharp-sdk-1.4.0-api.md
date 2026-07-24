# ADR-0001 — MCP C# SDK 1.4.0: adopted API surface

**Status:** Accepted · 2026-06-18 (locked by A01-T1 spike PR #1 `61f0946`, carried by A01-T2 PR #2 `9630daa`).
**Context source:** internal planning-workspace decision #1.

## Context
Darvoza is built on the official MCP C# SDK (`ModelContextProtocol*` 1.4.0, maintained with Microsoft).
The SDK surface moves between minors, so the spike's job was to pin the exact API the rest of the build
depends on, rather than re-deriving it per task.

## Decision
Adopt this surface and do not relitigate it in T3–T6:

- **Upstream client:** `McpClient.CreateAsync(transport)` (`ModelContextProtocol.Client`). `McpClientFactory`
  was removed in 1.4.0 — do not use it.
- **Server handlers:** low-level `WithListToolsHandler` / `WithCallToolHandler` delegates.
- **Tool listing:** `upstream.ListToolsAsync(...)` → project each `McpClientTool.ProtocolTool`.
- **Tool calls:** `CallToolAsync(CallToolRequestParams, ct)` — forward the incoming params object **verbatim**
  (no Arguments-dictionary conversion). Returns `ValueTask<CallToolResult>` end-to-end to avoid a
  wrap/unwrap allocation on the pass-through path.
- **Endpoint:** `app.MapMcp()` maps the Streamable-HTTP endpoint at the **root path `/`** (spec 2025-11-25).
  Clients connect to `http://localhost:<port>/`. Do not move it — documented client config depends on it.

## Consequences
- AC1–AC4 (spike) pass on this exact API; 90 upstream tools pass through name-identical; `core_list_projects`
  round-trips real data.
- T3/T4 wrap this surface via the seam in ADR-0002 rather than touching the SDK calls directly.

## Status notes
- Upstream server pinned `@azure-devops/mcp@2.7.0`, local/stdio, PAT = base64(`email:pat`). The
  local-vs-remote (Entra) upstream choice is a separate **open decision** (REQ-001 open-Q #4), to resolve in
  the A01-T6 writeup.
