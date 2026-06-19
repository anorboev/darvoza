# Darvoza demo — recording shot-list & narration (3–5 min)

The video that the Darvoza artifact stands on. It records the [`RUNBOOK.md`](RUNBOOK.md) walkthrough
against the **real** `darvoza-demo` Azure DevOps org, driven from a real MCP client (Claude
Desktop/Code) — satisfying REQ-001's "Claude driving Azure DevOps **through** the gateway."

**Before you hit record** (off-camera): complete RUNBOOK steps 0–3 — PAT set, `policy.yaml` copied, both
keys exported, gateway running on `:5000`, both `darvoza-analyst` / `darvoza-engineer` server entries
connected in the client, and a terminal already `tail -f`-ing the audit trail. Target length **3–5 min**.

| # | Shot | On screen | Narration beat |
|---|---|---|---|
| 1 | **Hook** (0:00–0:25) | Title card → the README "How it works" diagram | "Darvoza is a governance gate between any MCP client and Azure DevOps. Same tool, different role — watch the gate decide, and watch every decision get logged." |
| 2 | **Setup, fast** (0:25–1:00) | `policy.example.yaml` open: the `analyst` vs `engineer` allowlists; then the running gateway terminal | "Deny-by-default policy: analyst can read work items; engineer can also create them. The gateway already refused to start without this policy. Here it is, live on localhost:5000." |
| 3 | **Two clients, one URL** (1:00–1:30) | MCP client config showing `darvoza-analyst` + `darvoza-engineer` — same URL, different `X-Darvoza-Key`; both connected | "Two callers, identical endpoint — the only difference is the key each presents in the `X-Darvoza-Key` header. Notice the analyst doesn't even *see* the create tool: `tools/list` is filtered by policy." |
| 4 | **Analyst write → DENIED** (1:30–2:30) | In the analyst client: ask to create a work item → policy-denied result; cut to the audit `tail` showing the new line | "Analyst tries to create a work item. Denied — *before* it ever reaches Azure DevOps. And here's the audit line: `decision: deny`, role analyst, upstream null. The org never saw the request." |
| 5 | **Engineer write → ALLOWED** (2:30–3:30) | Switch to the engineer client: the *same* create request → success; show the new work item in the ADO board; cut to the audit `tail` | "Same request, engineer key. Allowed — forwarded upstream, work item created. New audit line: `decision: allow`, role engineer, `upstream: ok`." |
| 6 | **The trail** (3:30–4:30) | The two JSONL lines side by side | "Same tool, same arguments, opposite decision. Each line: who (role + a non-reversible key fingerprint), what, the decision, upstream status, latency. **No raw key, no PAT, no argument values** — ever. One line per call, allowed *and* denied: 100% coverage." |
| 7 | **Close** (4:30–5:00) | Repo URL / README | "Server-side, org-controlled policy plus a complete audit trail — in lightweight, readable .NET, for any MCP client. That's Darvoza." |

## Recording notes

- **Keep secrets off-camera.** The `X-Darvoza-Key` values, the PAT, and `.env` must never be on screen.
  Show the client config with the key field **redacted** or pre-filled before recording.
- **Pre-clear the trail** so only the demo's two lines appear: stop the gateway, delete/rotate
  `./audit/darvoza-audit.jsonl`, restart.
- **Pause on each audit line** long enough to read `decision` + `caller.role` + `upstream`.
- Hosting/linking the finished video is handled in **A01-T6** (writeup + publish).
