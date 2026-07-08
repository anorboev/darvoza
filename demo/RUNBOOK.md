# Darvoza demo runbook — two-role governance walkthrough

**Goal:** on a clean machine, stand up Darvoza in front of Azure DevOps and show the headline story —
**the same write tool denied for a read-only analyst and allowed for an engineer, each producing one
audit record** — in **under 15 minutes**.

This is the runbook the [shot-list](SHOTLIST.md) records against. The automated test
`tests/Darvoza.Gateway.Tests/E2E/LivePipelineE2ETests.cs` proves the same pipeline in CI with a fake
upstream; this runbook drives the **real** Azure DevOps org.

---

## 0. Prerequisites (~5 min)

| Need | Detail |
|---|---|
| **.NET 10 SDK** (LTS) | `dotnet --version` → `10.x` |
| **Node.js** | for the upstream `npx @azure-devops/mcp` server |
| **A free Azure DevOps org + project** | demo uses org `anorboev`, project `darvoza-demo` |
| **A least-privilege PAT** | scope **Work Items (Read & Write)** only — nothing else |
| **An MCP client** | Claude Desktop / Claude Code (or any streamable-HTTP MCP client that supports request headers) |

> Create the PAT in Azure DevOps → **User settings → Personal access tokens**. Keep it to Work-Items
> Read & Write so the demo's blast radius is exactly the story you're telling.

---

## 1. Configure the gateway (~3 min)

From the repo root:

```bash
# --- upstream (Azure DevOps) ---
export ADO_ORG="anorboev"
export AZURE_DEVOPS_EXT_PAT="<your least-privilege raw PAT>"   # never commit; held in-process, never logged

# --- policy: copy the example, then set the two caller keys it references ---
cp policy.example.yaml policy.yaml             # REQUIRED — the gateway refuses to start without a policy
export DARVOZA_KEY_ANALYST="analyst-demo-key-$(openssl rand -hex 8)"
export DARVOZA_KEY_ENGINEER="engineer-demo-key-$(openssl rand -hex 8)"

# --- pin the policy + audit paths to the repo root (NOT optional; see below) ---
export DARVOZA_POLICY_PATH="$PWD/policy.yaml"
export DARVOZA_AUDIT_PATH="$PWD/audit/darvoza-audit.jsonl"     # gitignored
```

Why each step:

- **`cp policy.example.yaml policy.yaml`** — the active `policy.yaml` is gitignored; a fresh clone has no
  policy and **deny-by-default means the gateway won't start** until you create one. The example already
  encodes the two demo roles: `analyst` (read-only: `wit_get_work_item`, `wit_my_work_items`,
  `wit_query_by_wiql`, …) and `engineer` (those reads **plus** `wit_create_work_item`).
- **`DARVOZA_POLICY_PATH` / `DARVOZA_AUDIT_PATH`** — without them, the gateway resolves both paths
  against the app's **content root, which under `dotnet run --project` is `src/Darvoza.Gateway`** — not
  the repo root you launched from. You would then hit a startup policy error (no
  `src/Darvoza.Gateway/policy.yaml`) — or, worse, tail an empty repo-root audit file while the real trail
  lands in `src/Darvoza.Gateway/audit/`. Pinning both to `$PWD` makes the demo deterministic.
- **`DARVOZA_KEY_ANALYST` / `DARVOZA_KEY_ENGINEER`** — the policy file names these env vars (`keyEnv`); the
  secret **values live in the environment, never in the file**. Each value is the `X-Darvoza-Key` a caller
  presents. An unset/empty key env var is a hard startup failure (never a silently-disabled caller).

Print the two keys so you can paste them into the client config in the next step:

```bash
echo "analyst  X-Darvoza-Key = $DARVOZA_KEY_ANALYST"
echo "engineer X-Darvoza-Key = $DARVOZA_KEY_ENGINEER"
```

> ⚠️ **Recording:** do this **before** you start recording, and clear your terminal scrollback
> afterward — these keys are throwaway demo values, but the habit (keys off-camera) is the point of the
> demo. The PAT is never printed.

---

## 2. Start the gateway (~1 min)

```bash
# from the repo root, in the same shell as step 1 (the exported env vars must be visible):
dotnet run --project src/Darvoza.Gateway        # listens on http://localhost:5000 (default Kestrel; or $ASPNETCORE_URLS)
```

The MCP endpoint is the **root path** `/` (streamable HTTP). On startup the gateway connects to the
upstream Azure DevOps MCP server **fail-fast** — if the PAT or org is wrong it exits immediately rather
than serving open. Leave it running; open a second terminal for the audit tail (step 4).

---

## 3. Point your MCP client at it — one entry per role (~3 min)

Darvoza identifies the caller by the **`X-Darvoza-Key`** request header. The cleanest demo configures
**two server entries** at the *same* URL with *different* keys, so you call the identical tool under each
role and watch the verdict flip. In a streamable-HTTP MCP client config (Claude Desktop / Claude Code),
add a `headers` map per server:

```jsonc
{
  "mcpServers": {
    "darvoza-analyst": {
      "type": "http",
      "url": "http://localhost:5000/",
      "headers": { "X-Darvoza-Key": "<paste $DARVOZA_KEY_ANALYST>" }
    },
    "darvoza-engineer": {
      "type": "http",
      "url": "http://localhost:5000/",
      "headers": { "X-Darvoza-Key": "<paste $DARVOZA_KEY_ENGINEER>" }
    }
  }
}
```

Restart/reconnect the client so both servers connect. Each will advertise only its role's allowed tools
(`tools/list` is filtered by policy) — already a visible difference: **analyst won't even list
`wit_create_work_item`.**

> If your MCP client build can't set per-server request headers, fall back to a thin demo client (a few
> lines that open an MCP HTTP transport with the header) — but the header-config path above is what makes
> the recording mechanical, so prefer it.

---

## 4. Run the two-role walkthrough (~3 min)

Open a second terminal to watch the trail live:

```bash
tail -f "$DARVOZA_AUDIT_PATH"     # the repo-root audit/darvoza-audit.jsonl you pinned in step 1
```

Then, in the MCP client:

1. **Analyst — denied write.** Using the **`darvoza-analyst`** server, ask the assistant to *create* a work
   item in `darvoza-demo` (tool `wit_create_work_item`).
   → The call is **denied by policy before reaching Azure DevOps**; the client gets a clean
   "policy denied" result. A new audit line appears with `"decision":"deny"` and `"upstream":null`.

2. **Engineer — allowed write.** Switch to the **`darvoza-engineer`** server and make the *same* create
   request.
   → The call is **forwarded** to Azure DevOps and the work item is created. A new audit line appears with
   `"decision":"allow"` and `"upstream":{"status":"ok"}`.

3. **Show the trail.** Point at the two new JSONL lines — same tool, same args shape, **opposite decision** —
   each carrying the caller role + a non-reversible key fingerprint, with **no raw key, no PAT, and no
   argument values** written (the `keyFingerprint`/`sha256`/`ts` values below are **illustrative** — your
   run produces different digests):

   ```json
   {"ts":"…","tool":"wit_create_work_item","caller":{"role":"analyst","keyFingerprint":"a1b2c3d4"},"decision":"deny","reason":"…","args":{"keys":["project","title"],"count":2,"sha256":"…"},"upstream":null,"latencyMs":1}
   {"ts":"…","tool":"wit_create_work_item","caller":{"role":"engineer","keyFingerprint":"e5f6a7b8"},"decision":"allow","reason":null,"args":{"keys":["project","title"],"count":2,"sha256":"…"},"upstream":{"status":"ok"},"latencyMs":214}
   ```

That is the whole story: **server-side, org-controlled policy + a 100%-coverage audit trail**, independent
of which MCP client drove it.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Gateway exits on startup with a policy error | You skipped `cp policy.example.yaml policy.yaml`, forgot `DARVOZA_POLICY_PATH` (without it the gateway looks in `src/Darvoza.Gateway/`, not the repo root), or a `keyEnv` var (`DARVOZA_KEY_ANALYST`/`ENGINEER`) is unset/empty. |
| Gateway exits with an upstream connection error | `ADO_ORG` wrong or `npx` can't launch. Note: the fail-fast start validates the local MCP handshake only — a bad PAT does **not** fail here; it surfaces on the first real call. |
| Every call is denied, even reads (audit shows `"role":null`) | The client isn't sending `X-Darvoza-Key`, or the key doesn't match the env value — watch for an invisible trailing `\r` if the keys were sourced from a CRLF-ended file. Re-check the `headers` map. |
| Engineer's create returns `Failed request: (401)` | The PAT lacks the Work Items **Write** scope — Azure DevOps reports a missing *scope* as 401 (not 403), and reads can still succeed. Regenerate the PAT with **Work Items (Read & Write)**. |
| `tools/list` shows fewer tools than the policy allows | Upstream tool names drifted (the server is in public preview); deny-by-default hides unknown names. Diff `policy.yaml` against the live surface — the example is verified against `@azure-devops/mcp` 2.7.0. |
| No audit lines appear | You're tailing a different file than the gateway writes. `tail -f "$DARVOZA_AUDIT_PATH"` in the step-1 shell; without that env var the trail lands in `src/Darvoza.Gateway/audit/`. |
