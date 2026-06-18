# `Darvoza` - Claude Code operating contract

This file overrides any conflicting rule in the user-level global
`~/.claude/CLAUDE.md` for this repo. Project rules win locally; global
rules cover everything not addressed here.

## Project + loop context (source of truth)

Darvoza is a .NET MCP governance gateway (per-role tool policy + audit) in front of Microsoft's
official Azure DevOps MCP server. The **Darvoza-scoped brief, board, and build order** live in the
planning workspace at
`C:/Projects/consulting-pivot/Consulting Pivot — HQ/artifacts/_dev/darvoza/CLAUDE.md`
(+ program docs `…/pm/requirements/REQ-001-*`, `…/pm/change-requests/CR-001-*`). This repo is the
**code side** of the Cowork↔CC loop: tasks arrive at the workspace's `_cc-tasks/`, run via
`/cowork-task`, and progress is written back to its `_cc-progress/`.

## TDD non-negotiables

- Red, green, refactor. No `.skip`, no `.todo`, no rewriting the test to match the bug.
- No mocking the system under test. Real database in integration tests (testcontainers acceptable).
- See `.claude/rules/tests.md` (auto-applies to test files).

## Git rules (enforced by hooks)

- **Merge is REVIEW-GATED, not auto-merge.** CC may push feature branches, open PRs, and run
  `@pr-reviewer` + `@security-reviewer`, but **must NOT run `gh pr merge` automatically.** Merge to
  `main` is a separate, human-approved step after the review/security gate passes.
- **Repo is PRIVATE until A01-T6** (after the security pass), then goes public. Don't change
  visibility before then.
- The `block-bad-git-push.ps1` hook refuses `--force`, `--force-with-lease`, and `--no-verify`.
  Direct-push-to-`main` is left to GitHub branch protection (enable it on the remote).
- Conventional Commits. Standard types: `feat`, `fix`, `chore`, `docs`,
  `refactor`, `test`, `ci`, `perf`, `build`, `revert`.
- Daily work in worktrees under `.worktrees/` if your workflow uses them.

## Deny list

- No `git push --force`, `git push --force-with-lease`, `git reset --hard origin`.
- No reading `.env`, `.credentials.json`, `.aws/`, `.azure/`, `.ssh/`.
- No production credentials in any tracked file (including `.env.example` -
  that file is shape-only).
- No `--no-verify` on git commit or git push (bypasses lefthook / git hooks).
- No pinned model versions in sub-agent / skill frontmatter - tier aliases only
  (`opus`, `sonnet`, `haiku`).

## Model routing

- **Opus** - architecture, security audit, RLS authoring, cross-cutting refactor,
  cc-progress-writer, doc-consistency-auditor.
- **Sonnet** - day-to-day coding, refactors, tests, schema migration, pr-reviewer.
- **Haiku** - formatting, lint, simple renames, routing decisions, opus-router.
- See the global `~/.claude/agents/opus-router.md` (haiku) for routing decisions;
  invoke via UserPromptSubmit hook or `@opus-router`.

## Plan mode

Required for any change >50 LOC, or any touch to: schema migrations,
authentication / authorization, payment posting, multi-tenant guard policies,
or any file the project's planning workspace marks as locked in
`C:/Projects/consulting-pivot/Consulting Pivot — HQ/artifacts/_dev/darvoza/CC_AUTONOMY_PLAYBOOK.md`.

## When in doubt

Ask. Or invoke `/handoff-rich` and start a fresh session with the right model
tier (consult `/opus-vs-sonnet` first if uncertain).
