# Darvoza

**An MCP governance gateway for Azure DevOps, in .NET — for any MCP client.**
Per-role tool policy, deny-by-default, full audit trail.

> _"Darvoza" — Uzbek for "gate."_ A thin .NET service that sits between any MCP client
> (Claude Code / Claude Desktop, VS Code Copilot, and other MCP-compatible assistants) and
> Microsoft's official Azure DevOps MCP server, so a regulated enterprise can roll out agentic
> access to Azure DevOps **without** giving every user unscoped write access to the org.
> Because Darvoza is itself a standard streamable-HTTP MCP server, any MCP client consumes it
> identically — examples below lead with Claude.

> **Status:** 🔵 build in progress (Sprint A; A01-T1 spike + A01-T2 skeleton merged, A01-T3 next;
> ship target ~2026-07-27). This is a focused open-source *reference implementation* — consulting
> proof-of-work, not a product launch.

## Why this exists

Client-side permissioning (e.g. an assistant's own allow/deny lists) isn't centrally enforced
or auditable; Azure API Management can govern MCP traffic but is heavyweight platform config.
Darvoza demonstrates the enterprise gap in between: **server-side, org-controlled policy + a
100%-coverage audit trail, in lightweight readable .NET — independent of which MCP client your
teams use.**

## How it works

```
any MCP client ──streamable-HTTP──▶  Darvoza gateway  ──stdio──▶  official azure-devops-mcp  ──▶  Azure DevOps
(Claude lead /                       │ per-role tool scoping
 VS Code Copilot / …)                │ deny-by-default
                                     └ append-only JSONL audit (every call, allowed AND denied)
```

- **Front leg:** streamable-HTTP MCP server (`ModelContextProtocol.AspNetCore` 1.4.0). Endpoint is
  the root path `/` (`MapMcp()` default; Streamable HTTP spec 2025-11-25).
- **Upstream leg:** MCP client over stdio to `microsoft/azure-devops-mcp` — pinned npm
  **`@azure-devops/mcp@2.7.0`**, launched `npx -y @azure-devops/mcp <org> --authentication pat`.
- **Policy:** declarative `policy.yaml` — roles → tool allowlists, deny-by-default, caller→role via per-caller API key.
- **Audit:** structured JSONL — caller, role, tool, arg summary/hash, allow/deny, upstream status, latency, UTC timestamp.

## Quickstart

> Requires .NET 10 SDK (LTS) + Node (for the upstream `npx` server) + an Azure DevOps org with a least-privilege PAT.

Secrets load from real env vars, or from a gitignored `.env` discovered by walking up from the
working dir **to the repo/solution root** — the search is bounded and never reads a `.env` outside
the project tree. Values already set in the environment take precedence over the `.env`.

```bash
export ADO_ORG="your-org"
export AZURE_DEVOPS_EXT_PAT="<least-privilege raw PAT>"   # never commit
dotnet run --project src/Darvoza.Gateway        # listens on http://localhost:5000 by default
# then point any MCP client (Claude Code/Desktop, VS Code Copilot, …) at  http://localhost:5000/
```

> **PAT handling:** the upstream `@azure-devops/mcp` `pat` mode reads `PERSONAL_ACCESS_TOKEN`
> whose value must be **base64 of `email:pat`**. Darvoza accepts either: a raw PAT in
> `AZURE_DEVOPS_EXT_PAT` (it base64-encodes it in-process for the upstream), or a pre-encoded
> `PERSONAL_ACCESS_TOKEN` (passed through). The token is only ever held in-process, never logged.

> **Pinned versions:** .NET `net10.0` · NuGet `ModelContextProtocol` + `ModelContextProtocol.AspNetCore`
> `1.4.0` · npm `@azure-devops/mcp@2.7.0`.

## Scope (v1 / MVP)

In: streamable-HTTP front · stdio upstream · YAML policy · JSONL audit · two-role demo · writeup.
Out (roadmap, called out in the writeup): approval gates / human-in-the-loop, full OAuth 2.1
resource-server compliance (RFC 9728 / Entra token validation), remote Entra-backed upstream,
multi-server federation, UI, rate limiting, content-safety filtering.

Full requirement: `../../../pm/requirements/REQ-001-claude-ado-governance-gateway.md`.

## Build order

| Task | What |
|---|---|
| A01-T1 | Spike: SDK-to-SDK passthrough (list + call round-trip). ✅ done (PR #1) |
| A01-T2 | Gateway skeleton: HTTP front + stdio upstream client. ✅ done (PR #2) |
| A01-T3 | Policy engine (YAML roles/allowlists, deny-by-default, caller keys). ← current |
| A01-T4 | Audit logging (JSONL, 100% coverage incl. denials) |
| A01-T5 | Demo: throwaway ADO org + two-role script + 3–5 min video |
| A01-T6 | README + technical writeup + security review + publish |

## Security

Deny-by-default; secrets via env only; no credentials in the repo. Authorization-boundary
code → a security review pass runs before this repo is made public (A01-T6).

## License

TBD before publish (MIT or Apache-2.0).
