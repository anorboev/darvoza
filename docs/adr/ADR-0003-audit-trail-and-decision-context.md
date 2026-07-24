# ADR-0003 — Audit trail: outermost decorator + ambient decision context

**Status:** Accepted · 2026-06-18 (A01-T4).
**Context source:** task `_cc-tasks/05-audit-logging.md`; builds on ADR-0002.
**Binding on:** A01-T4 (audit), A01-T5 (demo narrative), A01-T6 (writeup).

## Context
The headline NFR: **every** tool call through Darvoza — allowed AND denied — must produce **exactly one**
structured JSONL audit record. Audit is added as the **outermost `IUpstreamToolClient` decorator**
(ADR-0002), wrapping the T3 `PolicyEnforcingToolClient`, so it observes both forwarded calls and policy
denials. The hard problem: the audit decorator only receives a `CallToolResult` back from the policy stage,
and a policy denial (`IsError == true`, no upstream hit) is otherwise indistinguishable from an
allowed→upstream-error (`IsError == true`, from upstream). It also needs the denial *reason* and the caller
role, which the result does not carry.

## Decision

### 1. Carry the decision through an ambient `ICallDecisionContext`, not the result
`CallToolResult` **cannot be subclassed** (its base `Result` has a private constructor — "prevent external
derivations"), and stuffing the decision into the protocol `Meta` would leak it to the client (the denial
result is deliberately non-leaky). So the decision travels out-of-band:

- The audit decorator creates a mutable `CallDecisionBox` per call and **flows it down** via
  `ICallDecisionContext` (backed by `AsyncLocal<CallDecisionBox?>`) before invoking the inner client.
- The policy decorator reads `Current` and **records its decision** (allow/deny, reason, role, caller
  fingerprint) into the box. This *publishes* the decision it already made — it adds no policy logic.
- The audit decorator reads the box **through its own reference** after the call and writes one record.

**Why the box, not just AsyncLocal reads:** an `AsyncLocal<T>` value flows reliably to callees, but a
callee's write does **not** propagate back to the caller across an await. A shared mutable box sidesteps
that — the outer decorator holds the reference directly. Both the context and the decorators are
**singletons** (G-09 #1); the `AsyncLocal` slot isolates concurrent calls.

### 2. Record schema (one JSONL line per `CallToolAsync`)
`ts` (UTC ISO-8601), `tool`, `caller` (`role` + non-reversible `keyFingerprint`), `decision`
(`allow`/`deny`), `reason` (deny only), `args` (redacted: key names + count + SHA-256 digest — **never the
values**), `upstream` (`{status: ok|error}`, `null` on deny), `latencyMs`. (`decision` also has a defensive
`"unknown"` value for the should-not-occur case where the policy stage never published a decision — e.g. a
future reordering that lets the inner call fault first — so a missing decision can never read as a granted
call.) **No raw secret is ever written** — not the caller key, not the PAT, not unredacted arguments. The caller fingerprint is a truncated
digest of the caller key, computed by the policy stage (which already holds the key), so the audit stage
never touches the raw secret. `ListToolsAsync` is **not** a tool call and is not audited.

> **Amendment (A01-T6e, G-17 #1):** originally the fingerprint was an unsalted truncated SHA-256
> (8 hex / 32 bits). It is now a truncated **HMAC-SHA256 under a per-deployment salt** (16 hex /
> 64 bits): `DARVOZA_FINGERPRINT_SALT` when configured (fingerprints stable across restarts), else a
> fresh random salt at startup (fingerprints correlate within a run only). The salt is never logged
> and never written to the trail. This breaks cross-deployment fingerprint correlation and
> dictionary-matching of published trails against guessed keys. See `Audit/CallerFingerprint.cs`.

### 3. Fail-closed on write failure
If the record cannot be written, the call does **not** return a successful result — a non-leaky error
result is returned instead. This preserves the governance guarantee: **no unaudited success is ever
served.** Trade-off: an unwritable audit trail takes the gateway down (availability), and for an allowed
*mutating* call the upstream side-effect has already committed before the failed write, so the caller is
told it failed though the action occurred. This is inherent to after-the-fact audit and is accepted;
mitigate operationally with audit-disk monitoring.

### 4. Writer
`JsonlAuditSink` (append-only, `FileShare.Read` so the trail can be tailed live) is a container-owned
singleton (`IAsyncDisposable`, flushed/closed on shutdown — same lifecycle posture as the upstream client).
A `SemaphoreSlim(1,1)` serializes writes so concurrent calls never interleave a line, and each record is
flushed before the write returns (durability over throughput — fail-closed needs the bytes on disk). The
path is env-configurable (`DARVOZA_AUDIT_PATH`), defaulting to the gitignored `audit/` directory.

## Alternatives considered
- **Typed denial result (`PolicyDeniedResult : CallToolResult`)** — impossible; `Result` forbids external
  derivation, and a subclass member would be dropped by the SDK's source-generated serialization anyway.
- **Audit re-reads `Policy` to recompute the decision** — rejected: re-deciding (vs. recording) duplicates
  policy logic in the audit stage and drifts if policy logic changes (task constraint #2).
- **Fail-open on write failure** — rejected: it would serve unaudited successes, defeating the 100%-coverage
  NFR that is the whole point of the artifact.
