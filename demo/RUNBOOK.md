# Darvoza demo runbook — two-role governance walkthrough

**Goal:** on a clean machine, stand up Darvoza in front of Azure DevOps and show the headline story —
**the same write tool denied for a read-only analyst and allowed for an engineer, each producing one
audit record** — in **under 15 minutes**.

This is the runbook the recording docs record against — the current one is the
[DEMO-RUN-SHEET](DEMO-RUN-SHEET.md) (v2, silent take + captions, one-click prep); the earlier
[shot-list](SHOTLIST.md) keeps the per-shot rationale and caption text it builds on. The automated test
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
read -rs -p "PAT: " AZURE_DEVOPS_EXT_PAT && export AZURE_DEVOPS_EXT_PAT   # prompted, so it stays out of ~/.bash_history
# zsh (macOS default) has no -p: use  read -s "AZURE_DEVOPS_EXT_PAT?PAT: " && export AZURE_DEVOPS_EXT_PAT

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
  against its **working directory — which `dotnet run --project` sets to `src/Darvoza.Gateway`**, not
  the repo root you launched from. You would then hit a startup policy error (no
  `src/Darvoza.Gateway/policy.yaml`) — or, worse, tail an empty repo-root audit file while the real trail
  lands in `src/Darvoza.Gateway/audit/`. Pinning both to `$PWD` makes the demo deterministic.
- **`DARVOZA_KEY_ANALYST` / `DARVOZA_KEY_ENGINEER`** — the policy file names these env vars (`keyEnv`); the
  secret **values live in the environment, never in the file**. Each value is the `X-Darvoza-Key` a caller
  presents. An unset/empty key env var is a hard startup failure (never a silently-disabled caller).

<details>
<summary><b>PowerShell equivalents</b> (Windows — <code>export</code> is a bash-ism and will not work)</summary>

```powershell
# --- upstream (Azure DevOps) ---
$env:ADO_ORG = "anorboev"
# Prompt for the PAT rather than typing it as a literal: PSReadLine writes every command line verbatim
# to ConsoleHost_history.txt, so a literal assignment leaves the PAT in cleartext on disk after the demo.
$env:AZURE_DEVOPS_EXT_PAT = [System.Net.NetworkCredential]::new('', (Read-Host -AsSecureString "PAT")).Password

# --- policy: copy the example, then set the two caller keys it references ---
Copy-Item policy.example.yaml policy.yaml
$env:DARVOZA_KEY_ANALYST  = "analyst-demo-key-"  + [guid]::NewGuid().ToString("N").Substring(0,16)
$env:DARVOZA_KEY_ENGINEER = "engineer-demo-key-" + [guid]::NewGuid().ToString("N").Substring(0,16)

# --- pin the policy + audit paths to the repo root ---
$env:DARVOZA_POLICY_PATH = "$PWD\policy.yaml"
$env:DARVOZA_AUDIT_PATH  = "$PWD\audit\darvoza-audit.jsonl"
```

⚠️ These are **process-scoped**: every later step (starting the gateway, running the rogue caller,
tailing the trail) must happen in **this same PowerShell window**, or open new ones and re-set the vars.
`export` in Git Bash and `$env:` in PowerShell do **not** see each other.

</details>

Print the two keys so you can paste them into the client config in the next step:

Prefer the clipboard over the screen. Copy **one at a time** — the clipboard holds a single value, so
running both lines back to back leaves you only the second:

```bash
printf %s "$DARVOZA_KEY_ANALYST" | clip.exe         # macOS: pbcopy · Linux: xclip -selection clipboard
# …paste it into the analyst server entry, THEN come back for the engineer key:
printf %s "$DARVOZA_KEY_ENGINEER" | clip.exe
```

```powershell
Set-Clipboard -Value $env:DARVOZA_KEY_ANALYST       # paste, then repeat for the engineer key
```

> ⚠️ **Before recording, turn off clipboard sync and clear clipboard history** (Windows: Settings →
> System → Clipboard; Win+V retains entries and can sync them to your Microsoft account). A caller key in
> clipboard history outlives the shell, and the Win+V panel opening mid-take would put it on screen.

If you do print them instead, they are working caller credentials in your scrollback — see the teardown
note below.

> ⚠️ **Recording:** do this **before** you start recording, and clear your terminal scrollback
> afterward — these keys are throwaway demo values, but the habit (keys off-camera) is the point of the
> demo. The PAT is never printed.
>
> 🧹 **Teardown — do this after the last take, before publishing anything:**
> 1. **Revoke the PAT** in Azure DevOps → User settings → Personal access tokens, and delete its line
>    from the repo-root `.env` if you keep one there. It is the only real credential in the demo, and
>    A01-T6 publishes the repo and the video.
> 2. **Discard both caller keys** — if they live in the gitignored repo-root `.env` (the one-click
>    script's workflow), rotate or delete those `DARVOZA_KEY_*` lines; then close the demo shells,
>    delete the two server entries from the client config, and **clear the clipboard and its history**
>    (`Set-Clipboard -Value ' '` / `echo -n | clip.exe`, then Win+V → Clear all). The clipboard and the
>    `.env` file both survive the shell you just closed. They are live keys until all of that is done.
> 3. **Re-watch the footage for leaks** before upload: scrollback, the client config pane, and any frame
>    where a key or the PAT could have been on screen. The audit trail itself is safe to show — it carries
>    only a role and a non-reversible fingerprint, never the key.

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
   {"ts":"…","tool":"wit_create_work_item","caller":{"role":"analyst","keyFingerprint":"a1b2c3d4"},"decision":"deny","reason":"…","args":{"keys":["fields","project","workItemType"],"count":3,"sha256":"…"},"upstream":null,"latencyMs":1}
   {"ts":"…","tool":"wit_create_work_item","caller":{"role":"engineer","keyFingerprint":"e5f6a7b8"},"decision":"allow","reason":null,"args":{"keys":["fields","project","workItemType"],"count":3,"sha256":"…"},"upstream":{"status":"ok"},"latencyMs":214}
   ```

That is the whole story: **server-side, org-controlled policy + a 100%-coverage audit trail**, independent
of which MCP client drove it.

---

## 5. The rogue caller — showing the *call-level* deny (~2 min)

Step 4.1 has a subtlety worth putting on camera. Because `tools/list` is filtered by policy, a
**well-behaved** client like Claude Desktop never actually *attempts* `wit_create_work_item` as the
analyst — it can't see the tool, so it simply reports that it isn't available. That is the gateway
working, but it demonstrates the *listing* filter, not the *call* denial.

`demo/tools/RogueCaller` is a deliberately impolite MCP client: it **skips `tools/list` entirely** and
issues `tools/call` for a tool it was never offered — exactly what a compromised or malicious client
would do. Deny-by-default is enforced on the call as well as on the listing, so it is denied and logged.

```bash
# any shell: it reads the key from the environment, falling back to the repo-root .env
# (the same bounded DotEnvLoader the gateway uses, source-linked — an exported var still wins):
dotnet run --project demo/tools/RogueCaller
```

```text
rogue caller -> http://localhost:5000/
  identity : X-Darvoza-Key from $DARVOZA_KEY_ANALYST   (value never printed)
  tool     : wit_create_work_item   (calling it directly — tools/list is deliberately NOT requested)
  args     : project, workItemType, fields

DENIED (or upstream error) — gateway response:
  Policy denied: tool 'wit_create_work_item' is not permitted.
```

…and one new line in the trail: `"decision":"deny"`, `"caller":{"role":"analyst",…}`, `"upstream":null`.

Run the identical invocation as the engineer and it is forwarded and the work item is created:

```bash
dotnet run --project demo/tools/RogueCaller -- --key-env DARVOZA_KEY_ENGINEER
```

This doubles as the **terminal-only fallback** for the whole demo: if the desktop client misbehaves on
the day, shots 4a/4b and 5 can be produced entirely from two terminal invocations plus the audit tail.

| Flag | Default | Notes |
|---|---|---|
| `--url` | `http://localhost:5000/` | the gateway's root MCP endpoint. **Loopback only** (http/https, no embedded credentials, redirects disabled) — the caller key is attached to every request, and this tool only ever targets a local gateway |
| `--key-env` | `DARVOZA_KEY_ANALYST` | **name** of the env var holding the key — never the key itself. Must be a `DARVOZA_KEY_*` name, so a shell-expanded typo is rejected without echoing the key, and the tool can't be pointed at your PAT |
| `--tool` | `wit_create_work_item` | any tool name; it is sent whether or not policy allows it |
| `--title` | `Rogue attempt` | work-item title (sent as `System.Title` inside `fields`) |
| `--arg k=v` | — | repeatable; supplying any `--arg` replaces the default argument set **wholesale**, so `--title` is then ignored — put the title in your own `fields`. Any `--tool` other than `wit_create_work_item` requires `--arg` (the defaults are create-shaped and are not applied to another tool). |

> **Exit codes:** `0` allowed, `1` denied-or-upstream-error, `2` usage/connection error. Note the demo's
> *intended* analyst outcome exits **1** — don't chain this under `set -e` and read the deny as a failure.

> The default arguments (`project`, `workItemType`, `fields`) are verified against
> **`@azure-devops/mcp` 2.7.0** — note there is no top-level `title` argument; the title travels inside
> the required `fields` array. If the public-preview upstream renames these, override with `--arg`.

> The script never prints the caller key or the PAT, and it holds no credentials of its own — it reads
> one env var and puts it in a header, which is precisely what a real MCP client does.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| **Connector connects but shows _no tools_** | The key isn't reaching the gateway, or its value isn't in the gateway's environment — so the caller resolves to no role and deny-by-default filters `tools/list` to empty. **This is the gateway working correctly against an unauthenticated caller**, not a bug. Checklist: (1) the `keyEnv` vars were exported in the **same shell** that ran `dotnet run` — PowerShell `$env:`, *not* bash `export`, and a new terminal window does not inherit them; (2) the client's `headers` map spells `X-Darvoza-Key` exactly, with no stray spaces around the value; (3) the gateway was started **before** the client connected (it reads the key env vars once, at startup). Confirm with the rogue caller (step 5) — it prints which env var it read. **Note for operators:** an unauthorized `tools/list` currently leaves **no audit record** — the audit decorator wraps `CallToolAsync` only, by design (A01-T4 scope), so a caller probing with guessed keys is indistinguishable from this mistake. That is acceptable only while the front leg is loopback/trusted-network; it is part of the **G-10 / A01-T6b** front-leg-authentication gate. Tool *calls* are audited 100%, allowed and denied. |
| Rogue caller exits with `DARVOZA_KEY_… is not set` | The var is neither exported in this shell nor present in a repo-root `.env` (the caller loads `.env` with the same bounded loader as the gateway; its error message says whether a `.env` was found). Add the key to `.env`, or export it in this shell. |
| Gateway exits on startup with a policy error | You skipped `cp policy.example.yaml policy.yaml`, forgot `DARVOZA_POLICY_PATH` (without it the gateway looks in `src/Darvoza.Gateway/`, not the repo root), or a `keyEnv` var (`DARVOZA_KEY_ANALYST`/`ENGINEER`) is unset/empty. |
| Gateway exits with an upstream connection error | `ADO_ORG` wrong or `npx` can't launch. Note: the fail-fast start validates the local MCP handshake only — a bad PAT does **not** fail here; it surfaces on the first real call. |
| Every call is denied, even reads (audit shows `"caller":{"role":null,…}`) | The client isn't sending `X-Darvoza-Key`, or the key doesn't match the env value — watch for an invisible trailing `\r` if the keys were sourced from a CRLF-ended file. Re-check the `headers` map. |
| Engineer's create returns `Failed request: (401)` | The PAT lacks the Work Items **Write** scope — Azure DevOps reports a missing *scope* as 401 (not 403), and reads can still succeed. Regenerate the PAT with **Work Items (Read & Write)**. |
| `tools/list` shows fewer tools than the policy allows | Upstream tool names drifted (the server is in public preview); deny-by-default hides unknown names. Diff `policy.yaml` against the live surface — the example is verified against `@azure-devops/mcp` 2.7.0. |
| No audit lines appear | You're tailing a different file than the gateway writes. `tail -f "$DARVOZA_AUDIT_PATH"` in the step-1 shell; without that env var the trail lands in `src/Darvoza.Gateway/audit/`. |
