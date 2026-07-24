# Darvoza demo — record-ready run sheet (v2 — SILENT + CAPTIONS)

_v2 2026-07-22 — supersedes the voiced/bash v1. Changes: **no narration** (captions added in post — a cold doesn't block this, and most LinkedIn viewers watch muted anyway); **PowerShell syntax** (v1's bash `export` was a Windows trap); **shot 4 fixed** — rehearsal on 2026-07-22 showed the client never *attempts* a filtered tool, so the deny needs a rogue caller (see `_cc-tasks/07-demo-rogue-caller.md`; **record only after that script is merged**)._

Target on-camera length: **2.5–4 min**. One linear pass, no voice. Captions get overlaid in Clipchamp afterward.

---

## A. Off-camera prep (~10 min)

Secrets rule: **PAT, both key values, and `.env` never appear on screen.**
Much of this is already true from the 2026-07-22 pre-flight session (connectors configured + working).

- [ ] Easiest: run `demo\demo-oneclick.ps1` from the repo root — it does every step below
      (env, path pinning, audit pre-clear, tail + rogue windows, gateway) in the right order.
      Manual alternative: gateway env set **in the same PowerShell window** that will run it:
      `$env:ADO_ORG = "anorboev"` · `$env:AZURE_DEVOPS_EXT_PAT = "<PAT>"` ·
      `$env:DARVOZA_KEY_ANALYST = "<key>"` · `$env:DARVOZA_KEY_ENGINEER = "<key>"` ·
      **plus the path pins** `$env:DARVOZA_POLICY_PATH = "$PWD\policy.yaml"` and
      `$env:DARVOZA_AUDIT_PATH = "$PWD\audit\darvoza-audit.jsonl"` — without them the gateway
      resolves both against `src\Darvoza.Gateway\` (its `dotnet run` CWD) and fails startup /
      writes the trail where the tail isn't looking (RUNBOOK §1).
      _(PowerShell, not bash `export` — the v1 trap.)_
- [ ] `policy.yaml` present; start gateway: `dotnet run --project src/Darvoza.Gateway` → listening on `:5000`.
- [ ] Claude Desktop: both `darvoza-analyst` / `darvoza-engineer` connectors show connected; analyst's tool list has **no** `wit_create_work_item`. Key values redacted anywhere they'd be visible.
- [ ] Rogue-caller script available (from task 07): `demo/tools/` — dry-run it once **before** pre-clearing audit.
- [ ] **Pre-clear the trail:** stop gateway → delete/rotate `audit\darvoza-audit.jsonl` → restart gateway → reconnect client. (Today's pre-flight lines must not appear.)
- [ ] Second terminal: live tail — `Get-Content -Wait -Tail 10 .\audit\darvoza-audit.jsonl`
      (for shot 6, the pretty table: your `ConvertFrom-Json | Format-Table` one-liner.)
- [ ] Recorder set (OBS / `Win+G` Game Bar), 1080p, region covers client + terminals. **Mic OFF.**
- [ ] One silent dry run of Section B.

---

## B. Shot list — silent take, captions added in post

| # | Time | Do (on screen) | Caption (added in Clipchamp) |
|---|---|---|---|
| 1 | 0:00–0:20 | Title card → README "How it works" diagram | **Darvoza** — a governance gateway between any MCP client and Azure DevOps. Same tool, different role — the gate decides, and every decision is logged. |
| 2 | 0:20–0:50 | `policy.example.yaml` open: analyst vs engineer allowlists → running gateway terminal | Deny-by-default policy. Analyst: read-only. Engineer: read + create. The gateway refuses to start without a policy. |
| 3 | 0:50–1:20 | Connector list: both entries, same URL, different `X-Darvoza-Key` (redacted); open analyst's tool list | Two callers, one endpoint — the only difference is the key in the `X-Darvoza-Key` header. The analyst doesn't even **see** the create tool: `tools/list` is filtered by policy. |
| 4a | 1:20–1:50 | Analyst client: ask Claude to create a work item → Claude replies it has no create tool | Ask the analyst to create a work item. The client can't — the tool was never offered. |
| 4b | 1:50–2:20 | Terminal: rogue-caller script with the **analyst** key invokes `wit_create_work_item` directly → denied; cut to audit tail: new `deny` line | And if a caller ignores the list and invokes the tool anyway? **Denied at the gateway** — Azure DevOps never sees it. Audit: `decision: deny`, role analyst, upstream null. |
| 5 | 2:20–2:50 | Engineer client: same create request → success; show the new work item on the ADO board; audit tail: `allow` line | Same request, engineer key. Allowed — forwarded upstream, work item created. Audit: `decision: allow`, `upstream: ok`. |
| 6 | 2:50–3:30 | The deny + allow lines side by side in the pretty table view (role, key fingerprint, tool, decision, upstream, latency) | Every call, one line: who (role + non-reversible fingerprint), what, decision, upstream, latency. **No raw keys, no PAT, no argument values — ever.** Allowed *and* denied: 100% coverage. |
| 7 | 3:30–3:50 | Repo URL / README | Server-side, org-controlled policy + a complete audit trail — lightweight, readable .NET, for any MCP client. **That's Darvoza.** |

Hold each audit line ~2–3s so `decision` + `caller.role` + `upstream` are readable.

---

## C. Post-production (~30–40 min, Clipchamp — built into Windows 11)

- [ ] Import the recording; trim dead air between shots.
- [ ] Add the 8 captions above as text overlays (bottom third, high-contrast). Keep each on screen the full shot.
- [ ] Optional: 2× speed on any typing/waiting segments.
- [ ] Export 1080p MP4. Watch once end-to-end — **check no secret ever flashed on screen.**
- [ ] Rotate/delete the throwaway demo keys.
- [ ] Delete the demo's "Test" work item(s) from `darvoza-demo` if you want a clean board for reruns.
- [ ] Hosting/linking → **A01-T6** (writeup + publish).

---

_Fallback if per-server headers ever break in the client: the rogue-caller script can play BOTH roles (analyst deny + engineer allow) from terminals — the demo survives without the desktop client entirely._
