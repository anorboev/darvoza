# Darvoza demo — recording shot-list & narration (3–5 min)

The video that the Darvoza artifact stands on. It records the [`RUNBOOK.md`](RUNBOOK.md) walkthrough
against the **real** `darvoza-demo` Azure DevOps org, driven from a real MCP client (Claude
Desktop/Code) — satisfying REQ-001's "Claude driving Azure DevOps **through** the gateway."

**Before you hit record** (off-camera): complete RUNBOOK steps 0–3 — PAT set, `policy.yaml` copied, both
keys exported, gateway running on `:5000`, both `darvoza-analyst` / `darvoza-engineer` server entries
connected in the client, and a terminal already `tail -f`-ing the audit trail. Target length **3–5 min**.
Also build the rogue caller once off-camera (`dotnet build demo/tools/RogueCaller`) so shot 4b doesn't
open on a restore log.

**Silent + captions is the supported take.** Recording screen-only and adding the narration column below
as on-screen captions avoids a voice-over retake loop, and keeps the run mechanical: every shot is either
a terminal command or a window switch. The narration beats are written to work verbatim as caption text —
keep them short on screen and let the pauses (below) carry the timing. A voiced take is optional, not
required.

| # | Shot | On screen | Narration beat |
|---|---|---|---|
| 1 | **Hook** (0:00–0:25) | Title card → the README "How it works" diagram | "Darvoza is a governance gate between any MCP client and Azure DevOps. Same tool, different role — watch the gate decide, and watch every decision get logged." |
| 2 | **Setup, fast** (0:25–1:00) | `policy.example.yaml` open: the `analyst` vs `engineer` allowlists; then the running gateway terminal | "Deny-by-default policy: analyst can read work items; engineer can also create them. The gateway already refused to start without this policy. Here it is, live on localhost:5000." |
| 3 | **Two clients, one URL** (1:00–1:30) | MCP client config showing `darvoza-analyst` + `darvoza-engineer` — same URL, different `X-Darvoza-Key`; both connected | "Two callers, identical endpoint — the only difference is the key each presents in the `X-Darvoza-Key` header. Notice the analyst doesn't even *see* the create tool: `tools/list` is filtered by policy." |
| 4a | **Analyst write → the client can't even try** (1:30–2:00) | In the analyst client: ask to create a work item → the assistant reports the tool isn't available; show the short filtered tool list | "Analyst asks to create a work item. The client can't even *offer* it — `tools/list` was filtered by policy, so a well-behaved client never attempts the call." |
| 4b | **Rogue caller → DENIED at the call** (2:00–2:30) | Terminal: `dotnet run --project demo/tools/RogueCaller` → `Policy denied: tool 'wit_create_work_item' is not permitted.`; cut to the audit `tail` showing the new line | "But policy isn't a client-side suggestion. This script skips `tools/list` and invokes the tool directly — what a compromised client would do. Denied at the call, *before* it ever reaches Azure DevOps. Audit line: `decision: deny`, `caller.role: analyst`, upstream null. The org never saw the request." |
| 5 | **Engineer write → ALLOWED** (2:30–3:30) | Switch to the engineer client: the *same* create request → success; show the new work item in the ADO board; cut to the audit `tail` | "Same request, engineer key. Allowed — forwarded upstream, work item created. New audit line: `decision: allow`, `caller.role: engineer`, `upstream: ok`." |
| 6 | **The trail** (3:30–4:30) | The two JSONL lines side by side | "Same tool, same arguments, opposite decision. Each line: who (role + a non-reversible key fingerprint), what, the decision, upstream status, latency. **No raw key, no PAT, no argument values** — ever. One line per call, allowed *and* denied: 100% coverage." |
| 7 | **Close** (4:30–5:00) | Repo URL / README | "Server-side, org-controlled policy plus a complete audit trail — in lightweight, readable .NET, for any MCP client. That's Darvoza." |

## Recording notes

- **Keep secrets off-camera.** The `X-Darvoza-Key` values, the PAT, and `.env` must never be on screen.
  Show the client config with the key field **redacted** or pre-filled before recording.
- **Pre-clear the trail** so only the demo's two lines appear: stop the gateway, delete/rotate
  `"$DARVOZA_AUDIT_PATH"` (the repo-root `audit/darvoza-audit.jsonl` pinned in RUNBOOK step 1), restart.
- **Pause on each audit line** long enough to read `decision` + `caller.role` + `upstream`. With captions
  instead of voice-over, hold each line ~1s longer than feels necessary — the viewer is reading twice.
- **Terminal-only fallback.** If the desktop client misbehaves on the day, shots 4a/4b/5 can be produced
  entirely from the rogue caller (RUNBOOK step 5): analyst key → denied, `--key-env DARVOZA_KEY_ENGINEER`
  → created. You lose the 4a "the client can't even see it" beat, so cover it with a caption over the
  filtered tool list from shot 3.
- **Rogue caller output is camera-safe** — it prints the *name* of the key env var, never the key value,
  and it rejects a `--key-env` that was accidentally shell-expanded into a key rather than echoing it.
- Hosting/linking the finished video is handled in **A01-T6** (writeup + publish).
