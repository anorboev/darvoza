---
description: Auto-applies on any change to test files. TDD-strict — anti-cheat contract enforced by hooks + the test-skeptic agent.
globs: **/*.test.ts, **/*.test.tsx, **/*.spec.ts, **/*.spec.tsx
---

# Testing rules — TDD strict

## The cycle (non-negotiable)

1. **Red** — write a failing test that captures the new behavior. Run it; see it fail with a useful message.
2. **Green** — write the minimum production code to make it pass.
3. **Refactor** — clean up while green. Run tests after every change.

No commit lands without going through this cycle. The `tdd-guard` hook (PreToolUse) enforces it at the file level once the test runner is installed and emitting reporter state.

## Anti-cheat contract

The following are forbidden because they trade real signal for a green CI:

- **Horizontal slicing** — writing 10 tests up front before any production code. Tests must precede their corresponding production code by one cycle.
- **Rewriting the test to pass** when the production code was wrong. Either fix the production code, or surface the misalignment as a question.
- **`.skip`, `xit`, `it.todo`** — these are not "deferred", they're dead. Either keep the test or delete it with a commit message explaining why.
- **Mock of SUT** — never mock the system under test. Mock its collaborators (HTTP, DB, file system), not the function you're testing.
- **Expectation-matches-output** — copying actual output back as expected without verifying it's correct. Snapshot tests are guilty until proven innocent.
- **Deleted tests with surviving production behavior** — if a test goes away, the behavior it covered must also go away.

The `@test-skeptic` agent (opus) runs on the Stop hook and pre-commit and looks for exactly these patterns.

## Test layers

- **Unit** — pure logic, no IO. Whatever test runner you use (Vitest, Jest, pytest, xUnit, ...). Fast.
- **Integration** — module + real collaborators (database via testcontainers, queue, cache, etc.). Same runner.
- **E2E** — full stack via Playwright / Cypress / equivalent.

## DB tests

- **Real database, not mocked.** Mocked DBs miss real bugs (constraints, RLS, transaction semantics, index behaviour).
- Use a per-test transaction that rolls back on teardown, or testcontainers with a fresh schema.
- Migrations run before suite start; never rely on production seed data.

## Forbidden

- No mocking the SUT module (whatever `vi.mock` / `jest.mock` / `unittest.mock.patch` of the system under test looks like in your stack).
- No `.skip` / `xit` / `it.todo` (or whatever the equivalent "test exists but doesn't run" hatch is).
- No snapshot test without an inline comment explaining why a snapshot is the right test.
- No "smoke test that just calls the function and asserts no throw" — that's a coverage hack.

## See also

- `~/.claude/agents/test-skeptic.md` — global Stop-hook auditor (`@test-skeptic`).
- `tdd-guard` npm package — wraps the cycle enforcement; the `.claude/hooks/tdd-guard.js` hook reads its reporter state.
