#!/usr/bin/env node
// Scope: PROJECT — PreToolUse:Write|Edit|MultiEdit
// Wraps tdd-guard. Fails when a SUT edit lands with no failing test in the
// prior reporter run. exit 2 = block (TDD violation); exit 0 = allow.
//
// Stub by default. Wiring of the actual `tdd-guard` npm package activates
// once the project installs it and the test runner emits reporter state to
// node_modules/tdd-guard/.state.json. Until then this is a no-op that
// documents intent and exits 0.

const fs = require("node:fs");
const path = require("node:path");

let input = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => (input += chunk));
process.stdin.on("end", () => {
  try {
    const payload = JSON.parse(input || "{}");
    const filePath = payload?.tool_input?.file_path || "";

    // Only enforce on production source files (not test files, not config, not docs).
    if (!/\.(ts|tsx)$/.test(filePath)) return process.exit(0);
    if (/\.(test|spec)\.(ts|tsx)$/.test(filePath)) return process.exit(0);
    if (/[\\/](docs|evals|infra|supabase|__fixtures__|node_modules)[\\/]/.test(filePath)) return process.exit(0);

    // Phase 1 will check `node_modules/tdd-guard/.state.json` (or equivalent) for the
    // most-recent vitest reporter snapshot. If no failing test exists for the changed
    // surface, exit 2 with a TDD violation message.
    //
    // Until then: no-op.
    const projectDir = process.env.CLAUDE_PROJECT_DIR || process.cwd();
    const stateFile = path.join(projectDir, "node_modules", "tdd-guard", ".state.json");
    if (!fs.existsSync(stateFile)) {
      // tdd-guard not installed yet (Phase 0). Allow.
      return process.exit(0);
    }

    // Phase 1: real check goes here.
    return process.exit(0);
  } catch (err) {
    // Don't block on parser errors — the harness recovers better than a hung tool.
    process.stderr.write(`[tdd-guard] parse error: ${err.message}\n`);
    process.exit(0);
  }
});
