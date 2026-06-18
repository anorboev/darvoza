---
description: Load the Cowork-authored task spec at _cc-tasks/<slug>.md plus the fixed reading list, then enter plan mode against the full context.
argument-hint: <slug e.g. 15-track-c-slice4>
allowed-tools: Read, Glob, Grep, Bash(git status:*), Bash(git rev-parse:*), Bash(git log:*)
---

# `/cowork-task` — Cowork → CC handoff

Task slug: `$ARGUMENTS`

You are about to ingest a Cowork-authored task spec and plan its execution. This is the **primary entry point** for the Cowork+CC autonomous workflow.

## Step 1 — Validate the slug

`$ARGUMENTS` **must** match the regex `^[0-9]{2}-[a-z0-9-]+$` (e.g. `01-payment-flow`, `15-track-c-slice4`). If the argument is empty, contains `/`, `\`, `..`, or otherwise fails the regex, **stop and refuse** — do not proceed to any `Read` call. (Path traversal via the slug is the threat being blocked.)

The planning workspace path is configured in `.claude/settings.json` via `CC_PROGRESS_PLANNING_DOCS`. For Darvoza this is the HQ workspace `C:/Projects/consulting-pivot/Consulting Pivot — HQ/artifacts/_dev/darvoza` (note the spaces + em-dash — keep the path quoted in any shell call). The task file should be at `${CC_PROGRESS_PLANNING_DOCS}/_cc-tasks/$ARGUMENTS.md`.

If the task file doesn't exist, stop and tell the user (list `_cc-tasks/` so they can pick). Don't guess at the slug.

## Step 2 — Load the reading list (in order)

The Darvoza planning workspace is intentionally lean. Read these in order, no narration between reads, skipping any already read this session:

**Required (stop and report if missing):**

1. **Workspace brief + loop rules** - `${CC_PROGRESS_PLANNING_DOCS}/CLAUDE.md`
   - Darvoza-scoped brief, locked facts, scope discipline, loop rules, build order. (Serves as the project overview + the PM/BA contract for this lean setup.)

2. **Tactical board** - `${CC_PROGRESS_PLANNING_DOCS}/board.md`
   - Done / Doing / Next / Backlog / Open decisions. Identifies what `$ARGUMENTS` is about to flip.

3. **The task spec** - `${CC_PROGRESS_PLANNING_DOCS}/_cc-tasks/$ARGUMENTS.md`
   - The actual ask. Mission, scope, branch strategy, constraints, plan-mode deliverables, acceptance criteria.

**Read if present (skip silently if absent — they appear as the loop matures):**

4. **State of main** - `${CC_PROGRESS_PLANNING_DOCS}/_cc-progress/state-of-main.md`
   - Current `main` HEAD SHA + recent merges. Cross-check against `git log` before branching. (Empty until the first PR merges.)

5. **Decisions and gotchas** - `${CC_PROGRESS_PLANNING_DOCS}/_cc-progress/decisions-and-gotchas.md`
   - Numbered decisions (#1, …) and gotchas (G-01, …). Cite by number when one applies.

6. **PM/BA contract / autonomy playbook** - `${CC_PROGRESS_PLANNING_DOCS}/_meta/PM-BA-CONTRACT.md`, `${CC_PROGRESS_PLANNING_DOCS}/CC_AUTONOMY_PLAYBOOK.md`, `${CC_PROGRESS_PLANNING_DOCS}/README.md`
   - Fuller process docs if/when Cowork adds them. Until then, the workspace `CLAUDE.md` (item 1) is the governing contract.

Also consult the program source-of-truth when the task references it: `…/Consulting Pivot — HQ/pm/requirements/REQ-001-*` and `…/pm/change-requests/CR-001-*`.

## Step 3 — Verify repo state

Quick sanity checks before planning:

- `git status` - confirm working tree state. If dirty, surface it to the user.
- `git rev-parse origin/main` - confirm the SHA matches what state-of-main.md says it should be. If they diverge, that's a 🟠 RECONCILE - surface it.
- `git rev-parse --abbrev-ref HEAD` - which branch are you on? If not on `main` or a `.worktrees/` worktree, surface it.

## Step 4 — Plan

Produce a comprehensive plan that:

1. **Cites the task IDs** the work flips (from the board) and the acceptance criteria from the task spec.
2. **Lists files to create / edit / delete**, organized by phase.
3. **Identifies any 🟠 RECONCILE conflicts** between the task spec and the loaded context (decisions, board, README) - do NOT silently pick a side.
4. **References numbered decisions and gotchas** by number when one applies (e.g. "Per Decision #15, ...").
5. **Names the branch** you'll work in - if the task spec specifies a branch strategy, follow it; otherwise propose one matching the project's naming convention.
6. **Lists the agents you'll dispatch** during PR self-review (`@pr-reviewer`, `@security-reviewer`, and any project-specific ones the task spec calls for).
7. **Calls out anything load-bearing missing** from the task spec - surface as a question, do NOT pick a default and proceed.

End the plan by calling `ExitPlanMode` to request user approval.

## Step 5 — Execute (after plan approval) — REVIEW-GATED, no auto-merge

Darvoza overrides AFM-POS's blind auto-merge. After the user approves the plan:

1. TDD red-green (discipline per `.claude/rules/tests.md`; the `tdd-guard` hook is a no-op stub for .NET).
2. Conventional commits referencing the task slug.
3. `git push -u origin <branch>`.
4. `gh pr create` — body cites task slug, board items flipped, test count delta, deviations from spec.
5. Dispatch `@pr-reviewer` + `@security-reviewer` (+ `@test-skeptic` if coverage claims are non-trivial).
6. Address reviewer findings:
   - **Small** (within ~1 hour, no new architectural decisions): fix in same PR, push, re-dispatch.
   - **Large** (architectural change, scope expansion, new ADR needed): post PR comment, stop, wait for user.
7. **STOP — do NOT merge.** Post a PR summary + the reviewer/security verdicts and hand back to the
   user. **Merging to `main` is a separate, human-approved step** (the review/security gate). Do not
   run `gh pr merge` yourself unless the user explicitly tells you to in this session. **The repo stays
   PRIVATE until A01-T6.**
8. **After the human-approved merge** (whoever runs `gh pr merge`), the post-merge hook regenerates
   `_cc-progress/state-of-main.md`. Then invoke `@cc-progress-writer` to refresh `board-deltas.md` +
   `decisions-and-gotchas.md`, and optionally `@doc-consistency-auditor` to catch cross-doc drift.
9. If the merge closes a phase boundary, write `_cc-progress/YYYY-MM-DD-state-of-main.md` (frozen snapshot).

## What you do NOT do here

- **Skip the plan-mode gate.** Even for "obviously simple" tasks.
- **Skip TDD.** The `tdd-guard` hook will block you anyway.
- **Edit anything in the planning workspace outside `_cc-progress/`.** That surface is Cowork's.
- **Invent task IDs.** Every task item in your plan must exist in `board.md` (or in `_cc-progress/board-deltas.md` as a proposed-but-not-yet-applied delta).
- **Reference agents that don't exist.** Check `.claude/agents/` (project) or `~/.claude/agents/` (global) before naming one.

## Recovery

- **CC_PROGRESS_PLANNING_DOCS env var unset or pointing nowhere:** ask the user to run `init-project.ps1` (or to manually set the env var). Don't fall back silently.
- **Task file slug doesn't exist:** list the contents of `_cc-tasks/` so the user can pick the right one.
- **state-of-main.md is stale (last update > 24h ago) or missing:** invoke the hook manually via `pwsh -NoProfile -File .claude/hooks/update-cc-progress.ps1` and verify it regenerated before proceeding.
