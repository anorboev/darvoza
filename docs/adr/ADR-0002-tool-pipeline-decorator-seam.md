# ADR-0002 — Tool pipeline: decorator chain on a single upstream seam

**Status:** Accepted · 2026-06-18 (A01-T2 PR #2 `9630daa`).
**Context source:** `_cc-progress/decisions-and-gotchas.md` decisions #5 and #6.
**Binding on:** A01-T3 (policy), A01-T4 (audit).

## Context
Darvoza must add per-role tool **policy** (T3) and a full **audit** trail (T4) on top of a transparent
passthrough, without rewriting the request handlers each time. A01-T2 established the seam that makes this
clean and test-covered.

## Decision

### 1. One boundary interface, decorators stack on it
There is exactly one extensibility boundary: **`IUpstreamToolClient`**
(`src/Darvoza.Gateway/Upstream/IUpstreamToolClient.cs`). T2 ships one concrete implementation,
`McpUpstreamToolClient` (transparent pass-through). T3 and T4 are added as **decorators of this same
interface** wrapping the concrete client:

- **T3 (policy)** filters `ListToolsAsync` (return only tools the caller's role allows) and **short-circuits
  `CallToolAsync` with deny-by-default before the inner call**.
- **T4 (audit)** records around the inner call.

The request handlers (`PassthroughToolHandlers`) depend only on the **DI-resolved outermost
`IUpstreamToolClient`** (resolved per-call via `ctx.Services`), so they never change as decorators stack.
`CallToolAsync` returns `ValueTask<CallToolResult>` end-to-end (no wrap/unwrap allocation on the hot path).

### 2. Upstream lifecycle: container-owned singleton + fail-fast connect
The upstream `McpClient` is owned by `McpUpstreamToolClient`, registered as a container-constructed
**singleton** so the DI container disposes it on shutdown (tears down the stdio transport, kills the `npx`
child). Connection is established once at startup by `UpstreamConnectionInitializer` (IHostedService) in
`StartAsync`; **upstream-unreachable fails host startup** before Kestrel serves (correct for a governance
gateway — know the upstream is down before accepting traffic). `DisposeAsync` is race-safe (`_disposed`
flag + connect gate; `_client` is `volatile`).

## Consequences — constraints T3/T4 MUST honor (a.k.a. G-09)
1. **Decorator lifetime:** `IUpstreamToolClient` + `PassthroughToolHandlers` are registered as **singletons**.
   A per-request-scoped decorator under a singleton handler is a captive-dependency bug. Either register the
   T3/T4 decorators as singletons, or move `PassthroughToolHandlers` to `AddScoped` (it already resolves
   per-call via `ctx.Services`, so scoped is safe).
2. **Keep the concrete singleton registration:** `AddSingleton(_ => new McpUpstreamToolClient(...))` must
   remain even after decorators wrap the interface — `UpstreamConnectionInitializer` resolves the concrete
   type (not the decorated interface) for its connect lifecycle. Dropping it breaks startup connect.

Both constraints are mirrored in code comments at `Program.cs:52-58`.

## Alternatives considered
- Filtering/auditing inside the request handlers directly — rejected: couples "what we forward" to "what we
  decide", and handlers would change on every new concern.
