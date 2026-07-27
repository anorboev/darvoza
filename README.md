# Darvoza

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0_(LTS)-512BD4.svg)](https://dotnet.microsoft.com/)
[![MCP C# SDK](https://img.shields.io/badge/MCP_C%23_SDK-1.4.0-blue.svg)](https://www.nuget.org/packages/ModelContextProtocol)

**An MCP governance gateway in .NET — put per-role tool policy and a full audit trail in front of
any MCP server.** Deny-by-default, every call recorded.

> _"Darvoza" — Uzbek for "gate."_ A thin .NET service that sits between any MCP client
> (Claude Code / Claude Desktop, VS Code Copilot, and other MCP-compatible assistants) and any
> MCP server, so a regulated enterprise can roll out agentic access to a system of record
> **without** giving every user unscoped write access to it.
> Both legs are standard MCP: Darvoza is itself a streamable-HTTP MCP server, so any client
> consumes it identically, and it speaks stdio to whichever upstream you point it at.
>
> **The demonstrated case is Azure DevOps** — the demo, the video, and the default configuration all
> run against Microsoft's official Azure DevOps MCP server. Pointing it at your own server is a
> [config change](#use-it-with-another-mcp-server), not a fork.

> **Status:** ✅ v1 complete — gateway, policy engine, audit trail, demo assets, the pre-publish
> security pass (A01-T6), and the configurable upstream (A01-T7) have all landed. This is a focused
> open-source *reference implementation* — consulting proof-of-work, not a product launch. See the
> [demo](#demo) and the [security model](#security-model).

## Why this exists

Client-side permissioning (e.g. an assistant's own allow/deny lists) isn't centrally enforced
or auditable; Azure API Management can govern MCP traffic but is heavyweight platform config.
Darvoza demonstrates the enterprise gap in between: **server-side, org-controlled policy + a
100%-coverage audit trail, in lightweight readable .NET — independent of which MCP client your
teams use, and of which MCP server you put behind it.**

## How it works

```
any MCP client ──streamable-HTTP──▶  Darvoza gateway  ──stdio──▶  any MCP server  ──▶  your system of record
(Claude lead /                       │ per-role tool scoping      (demo: official
 VS Code Copilot / …)                │ deny-by-default             azure-devops-mcp)
                                     └ append-only JSONL audit (every call, allowed AND denied)
```

Policy and audit operate on **tool names** at a single decorator seam, so neither knows or cares which
server is upstream — only the launch path ever did, and since A01-T7 that is configuration.

- **Front leg:** streamable-HTTP MCP server (`ModelContextProtocol.AspNetCore` 1.4.0). Endpoint is
  the root path `/` (`MapMcp()` default; Streamable HTTP spec 2025-11-25).
- **Upstream leg:** MCP client over stdio to whichever server the policy file selects — see
  [Use it with another MCP server](#use-it-with-another-mcp-server). The default (and the demonstrated
  case) is `microsoft/azure-devops-mcp`, pinned npm **`@azure-devops/mcp@2.7.0`**. Launch contract for
  that profile: on Windows the gateway runs
  `node <npm>/bin/npx-cli.js -y @azure-devops/mcp@2.7.0 <org> --authentication pat` directly (never
  `npx.cmd`, so the batch file's own re-parse is avoided; set `DARVOZA_NPX_CLI_JS` for non-standard
  npm layouts); elsewhere plain `npx` (a real binary). The PAT travels only in the child's
  environment, never in argv.
- **Policy:** declarative `policy.yaml` — roles → tool allowlists, deny-by-default, caller→role via per-caller API key.
- **Audit:** structured JSONL — caller, role, tool, arg summary/hash, allow/deny, upstream status, latency, UTC timestamp.

## Quickstart

> Requires .NET 10 SDK (LTS). This quickstart uses the default Azure DevOps upstream, which also needs
> Node (for the upstream `npx` server) and an Azure DevOps org with a least-privilege PAT — neither is
> required if you [point Darvoza at a different MCP server](#use-it-with-another-mcp-server).

Secrets load from real env vars, or from a gitignored `.env` discovered by walking up from the
working dir **to the repo/solution root** — the search is bounded and never reads a `.env` outside
the project tree. Values already set in the environment take precedence over the `.env`.

```bash
export ADO_ORG="your-org"
export AZURE_DEVOPS_EXT_PAT="<least-privilege raw PAT>"   # never commit
cp policy.example.yaml policy.yaml                        # required — the gateway won't start without a policy
export DARVOZA_KEY_ANALYST="<analyst caller key>"         # the X-Darvoza-Key value bound to the analyst role
export DARVOZA_KEY_ENGINEER="<engineer caller key>"       # …and the engineer role (keys live in env, not the file)
export DARVOZA_POLICY_PATH="$PWD/policy.yaml"             # pin to the repo root — `dotnet run --project` runs the app with its working directory set to src/Darvoza.Gateway
export DARVOZA_AUDIT_PATH="$PWD/audit/darvoza-audit.jsonl"   # audit-trail file (gitignored)
dotnet run --project src/Darvoza.Gateway        # listens on http://localhost:5000 by default
# then point any MCP client (Claude Code/Desktop, VS Code Copilot, …) at  http://localhost:5000/
# …sending its per-caller key as the  X-Darvoza-Key  request header
```

> **Demo:** see the [Demo](#demo) section below for the two-role walkthrough and video.

> **PAT handling:** the upstream `@azure-devops/mcp` `pat` mode reads `PERSONAL_ACCESS_TOKEN`
> whose value must be **base64 of `email:pat`**. Darvoza accepts either: a raw PAT in
> `AZURE_DEVOPS_EXT_PAT` (it base64-encodes it in-process for the upstream), or a pre-encoded
> `PERSONAL_ACCESS_TOKEN` (passed through). The token is only ever held in-process, never logged.

> **Pinned versions:** .NET `net10.0` · NuGet `ModelContextProtocol` + `ModelContextProtocol.AspNetCore`
> `1.4.0` · npm `@azure-devops/mcp@2.7.0`.

## Policy (`policy.yaml`)

Darvoza enforces a declarative policy at the gateway, **deny-by-default**: a tool not explicitly
allowed for the caller's role is denied *before it reaches upstream*, and `tools/list` returns only the
caller-role's allowed tools. A caller identifies itself with a per-caller secret sent as the
`X-Darvoza-Key` request header; an unknown or missing key is denied. The file maps `roles → allow`
(exact tool-name allowlists) and `callers → role`, where each caller's key is supplied via a named
environment variable (`keyEnv`) — never written in the file. The gateway resolves the policy at startup
— `$DARVOZA_POLICY_PATH` if set, else a gitignored `policy.local.yaml` override, else `policy.yaml` —
and **fails to start** if it is missing, unparseable, or half-configured — it never starts open. See
`policy.example.yaml`.

```yaml
callers:
  - keyEnv: DARVOZA_KEY_ANALYST          # env var holding this caller's secret key (the X-Darvoza-Key value)
    role: analyst
roles:
  analyst:
    allow: [repo_list_repos_by_project, wit_get_work_item]   # every other tool is denied by default
```

## Use it with another MCP server

Darvoza governs whichever MCP server the **policy file** names. With no `upstream:` section it launches
the built-in `azure-devops` profile, which is what the demo and the quickstart above use. To point it at
your own server, give it a command and its arguments:

```yaml
# policy.yaml — governing some other stdio MCP server
upstream:
  command: node                                    # the executable Darvoza launches
  args: ["/srv/my-mcp-server/index.js", "--readonly"]   # one list element per argument
  passEnv: [MY_SERVER_TOKEN]                       # variable NAMES to forward; values stay in the env

callers:
  - keyEnv: DARVOZA_KEY_ANALYST
    role: analyst
roles:
  analyst:
    allow: [search_documents, get_document]        # your server's tool names; everything else denied
```

That is the whole change — no `ADO_ORG`, no PAT, no Node needed unless your server wants them. Policy
enforcement, the audit trail, and the `X-Darvoza-Key` role mapping work exactly as documented above,
because none of them ever knew which server was upstream.

Notes worth reading once:

- **`args` is a list, one element per argument.** Darvoza never splits a command string into arguments —
  that word-splitting step is what a shell does. A single string is rejected at startup. (This is about
  *Darvoza's* layer; on Windows the MCP SDK still wraps the launch in `cmd.exe /c` — see the
  [security model](#security-model).)
- **Put credentials in `passEnv`, not in `args`.** The resolved argv is logged once at startup, so a
  secret in `args` lands in your logs. A configured upstream gets a curated environment plus exactly the
  variables you name in `passEnv` — it does **not** inherit the gateway's environment, which holds your
  caller keys and the audit fingerprint salt. An unset `passEnv` variable fails startup rather than
  launching the server half-configured. **Darvoza also refuses to forward its own secrets** — naming
  `DARVOZA_KEY_*`, `DARVOZA_FINGERPRINT_SALT`, `PERSONAL_ACCESS_TOKEN` or `AZURE_DEVOPS_EXT_PAT` in
  `passEnv` fails startup, because handing an upstream a caller key would let it call back in as that
  role and undo the isolation it sits beside.
- **Startup tells you when the policy and the server disagree.** Any allow-listed tool name the connected
  server does not offer produces one warning — useful when a tool gets renamed upstream. It is a warning,
  not a failure: deny-by-default makes an absent tool harmless, and a governance gateway should not fall
  over on a benign version bump.
- **One upstream per gateway instance.** Fronting several servers at once is roadmap, not v1.

## Audit trail (JSONL)

Every `tools/call` through Darvoza writes **exactly one** structured JSON line — for all three outcomes:
allowed→upstream-ok, allowed→upstream-error, and policy-denied (the denied call is recorded and never
reaches upstream). This 100%-coverage trail is the headline guarantee. Records are **append-only** to a
configurable file (`DARVOZA_AUDIT_PATH`; default `./audit/darvoza-audit.jsonl` relative to the gateway
process's working directory — pin it explicitly, as in the quickstart above; gitignored). The audit
layer is the outermost decorator over the policy layer (see `docs/adr/ADR-0003`).

**No raw secret is ever written** — never the caller key, never the PAT, never unredacted argument values.
The caller is identified by role plus a non-reversible short fingerprint of its key — a truncated
HMAC-SHA256 under a **per-deployment salt** (`DARVOZA_FINGERPRINT_SALT` if configured, else generated
fresh at startup), so a published trail cannot be dictionary-matched against guessed keys and the same
key maps to different fingerprints on different deployments. The salt itself is never logged. Arguments
are summarized as their key names + count + a SHA-256 digest (the values are hashed, never stored). If a
record cannot be written, the call **fails closed** — no unaudited success is returned.

```json
{
  "ts": "2026-06-18T12:00:00.0000000+00:00",
  "tool": "wit_get_work_item",
  "caller": { "role": "analyst", "keyFingerprint": "a1b2c3d4e5f60718" },
  "decision": "allow",
  "reason": null,
  "args": { "keys": ["id", "project"], "count": 2, "sha256": "…" },
  "upstream": { "status": "ok" },
  "latencyMs": 42
}
```

On a policy denial, `decision` is `"deny"`, `reason` carries the non-leaky message, and `upstream` is `null`.

> The trail records roles, key fingerprints, and tool names — keep the audit directory on
> operator-private storage (the file is opened `FileShare.Read` so it can be tailed live, so file
> ACLs are the isolation mechanism, not sharing flags). Restrict it to the operating user:
> `chmod 700 audit && chmod 600 audit/darvoza-audit.jsonl` on Unix — the gateway warns at startup if
> the directory is group/world-accessible — or
> `icacls audit /inheritance:r /grant:r "%USERNAME%:(OI)(CI)F"` on Windows (no cheap reliable ACL
> check exists there, so Windows hardening is guidance, not a runtime tripwire). Multi-tenant
> isolation remains out of v1 scope.

## Demo

📹 **Video:** _3–5 minute two-role walkthrough — link coming with the launch post._
<!-- TODO(publish): replace with the hosted darvoza-demo-final.mp4 link -->

The demo shows the same write tool (`wit_create_work_item`) **denied for a read-only analyst and
allowed for an engineer** — each producing exactly one audit record — against a real Azure DevOps org.

- [`demo/RUNBOOK.md`](demo/RUNBOOK.md) — the full guided walkthrough (~15 min from a fresh clone).
- [`demo/demo-oneclick.ps1`](demo/demo-oneclick.ps1) — one-shot prep script: loads the gitignored
  `.env`, pins policy/audit paths, opens the audit-tail window, and starts the gateway.
- [`demo/tools/RogueCaller`](demo/tools/RogueCaller) — a deliberately *impolite* MCP client. Because the
  gateway filters `tools/list` per role, a **correct** client never even attempts a disallowed call —
  so the call-level deny was unobservable from any well-behaved client. The rogue caller skips
  `tools/list` and calls the write tool directly, exactly like a compromised client would — proving
  deny-by-default is enforced **on the call, not just on the listing**.

## Scope (v1 / MVP)

In: streamable-HTTP front · stdio upstream · YAML policy · JSONL audit · two-role demo · writeup.
Out (roadmap): approval gates / human-in-the-loop, full OAuth 2.1 resource-server compliance
(RFC 9728 / Entra token validation), remote Entra-backed upstream, multi-server federation, UI,
rate limiting, content-safety filtering.

### Roadmap note — local vs. remote (Entra) upstream

This note is about the **Azure DevOps** upstream specifically. Darvoza's upstream leg is stdio in v1
whichever server you configure; remote/HTTP upstreams are roadmap.

v1 deliberately targets Microsoft's **local/stdio** Azure DevOps MCP server with PAT auth. Microsoft
also ships a remote, Entra-backed variant and has signaled the local flavor retires when remote
reaches GA — at which point Darvoza's upstream leg migrates from stdio+PAT to streamable HTTP with
Entra token pass-through (the front leg and the policy/audit decorators are unaffected; the upstream
leg is one seam — `IUpstreamToolClient`). Related operational lesson from the build: the upstream is
in public preview and its tool names/argument shapes drift between versions, so the launch pins
`@azure-devops/mcp@2.7.0` and any version bump should be a deliberate, tested change.

## Build order

| Task | What |
|---|---|
| A01-T1 | Spike: SDK-to-SDK passthrough (list + call round-trip). ✅ done (PR #1) |
| A01-T2 | Gateway skeleton: HTTP front + stdio upstream client. ✅ done (PR #2) |
| A01-T3 | Policy engine (YAML roles/allowlists, deny-by-default, caller keys). ✅ done (PR #5) |
| A01-T4 | Audit logging (JSONL, 100% coverage incl. denials). ✅ done (PR #6) |
| A01-T5 | Demo: throwaway ADO org + two-role script + 3–5 min video. ✅ done (PRs #7–#9, #11) |
| A01-T6 | Security pass (constant-time key lookup, salted fingerprints, hardened upstream launch, front-leg review, audit ACLs) + README + publish. ✅ done |
| A01-T7 | Configurable upstream MCP server + generalized positioning (ADR-0004). ✅ done |

## Security model

**What Darvoza enforces:**

- **Deny-by-default, at both surfaces.** `tools/list` returns only the caller-role's allow-listed
  tools, and `tools/call` is denied *before touching upstream* for anything not allow-listed — proven
  end-to-end by the test suite and observable via the [rogue caller](demo/tools/RogueCaller).
- **Exactly one audit record per tool call, for every outcome** — allowed→ok, allowed→upstream-error,
  allowed→exception, denied. If the record cannot be written, the call **fails closed** rather than
  returning an unaudited success.
- **No raw secrets on disk.** Caller keys appear in the trail only as truncated, salted HMAC-SHA256
  fingerprints; argument values are digested, never stored; the PAT is env-only and never in argv.
- **Constant-time key resolution.** Caller keys are compared as fixed-width SHA-256 digests via
  `CryptographicOperations.FixedTimeEquals` over every entry — no timing signal on key content.
- **Non-leaky denials.** The deny result is byte-identical for "unknown key" and "known key, denied
  tool" (asserted by test), so the call surface is not a key-validity oracle.
- **Fail-fast configuration.** A missing/invalid policy, an unset caller-key env var, or an
  unreachable upstream refuses to start the host — the gateway never starts open.
- **The upstream command comes from the config file only.** Never from an environment variable, header,
  query string, or body — the launch is resolved before the web host is built, so no request can reach
  it. Argv is passed as an array: **Darvoza** never joins or splits it. (What the MCP SDK does below that
  on Windows is a documented non-goal — see below.)
- **A configured upstream does not inherit the gateway's environment.** It would otherwise receive every
  caller key and the audit fingerprint salt, letting a third-party server authenticate back into the
  front leg as any role. It gets a curated default environment plus exactly the variables named in
  `upstream.passEnv` (names in the file, values from the environment). The default Azure DevOps profile
  still inherits — it is the pinned, trusted package.

**The config file is a trust boundary.**

Whoever can edit `policy.yaml` can already define a role allow-listing every upstream tool and bind a
caller key to it — their authority over Darvoza's decisions is total before they touch the `upstream:`
section. Letting that same file name the upstream command therefore does not widen their power over the
gateway. To be precise rather than glib: arbitrary tool calls are not literally arbitrary code execution.
The honest form is that in every deployment shape Darvoza supports (single-tenant, operator-run,
loopback / trusted network), whoever can write the config file can also write the gateway's binaries or
its service definition, which already yields code execution. **This makes an existing boundary explicit
rather than creating one.**

The case that is *not* covered, stated plainly: a deployment where the config file is writable by a party
who cannot write the install directory — a config-management agent with a narrower ACL, a shared
operations volume. There, config-driven process launch **is** a privilege escalation. **Keep the policy
file owner-writable only**, with the same care as the binary. Full argument in
[`docs/adr/ADR-0004`](docs/adr/ADR-0004-configurable-upstream-and-config-trust-boundary.md).

**What Darvoza does NOT protect against (v1):**

- **Transport authentication.** `X-Darvoza-Key` is app-layer *authorization*. The deployment
  assumption is loopback / trusted network (the gateway warns at startup when bound wider); on an
  untrusted network, front it with TLS + network-level authentication.
- **Unauthenticated `tools/list` probing is unaudited.** The 100%-coverage guarantee is for tool
  *calls*; listing probes with guessed keys leave no trail in v1 (acceptable only under the loopback
  assumption; an explicit follow-up for any wider deployment).
- **Prompt injection / content safety.** Tool *results* pass through unmodified — a malicious work
  item description reaches the client. Governance here is about *which tools run*, not what they return.
- **A shell in the launch path on Windows.** The pinned MCP C# SDK rewrites every stdio launch to
  `cmd.exe /c <command> <args…>` on Windows (it applies its own escaping). Darvoza never builds a command
  line itself, and both the command and its arguments come from the config file rather than from request
  input — so this is not an injection path — but a shell *is* involved, the gateway warns about it at
  startup, and on Windows the strict `ADO_ORG` allowlist is load-bearing rather than defense-in-depth.
  Non-Windows spawns directly. Details in
  [`docs/adr/ADR-0004`](docs/adr/ADR-0004-configurable-upstream-and-config-trust-boundary.md).
- **A compromised upstream or host.** Darvoza trusts the upstream server it is configured to launch —
  the pinned `@azure-devops/mcp` package by default, or whatever you point it at — and the audit trail is
  only as private as the directory it lands in (see the ACL guidance above). Note the real bound of the
  environment isolation above: the child runs as the *same user*, so a **hostile** upstream can still read
  the parent's environment directly (`/proc/<ppid>/environ` on Linux, `PROCESS_VM_READ` on Windows). The
  isolation defeats accidental exposure and an upstream that merely reads its own `getenv`; it is not a
  sandbox. Run genuinely untrusted servers under a separate user or container.
- **Environment isolation for the *default* Azure DevOps profile.** A *configured* upstream is isolated
  (see the enforced-guarantees list above), but the built-in `azure-devops` profile still inherits the
  gateway's environment — so the official upstream also sees your caller keys and the audit fingerprint
  salt. It is the pinned, trusted package and this is unchanged v1 behaviour, but narrowing it is
  tracked, not done.

Architecture decisions are recorded in [`docs/adr/`](docs/adr/) (SDK surface, decorator seam,
audit + decision context, configurable upstream + the config-file trust boundary).

## License

[MIT](LICENSE).
