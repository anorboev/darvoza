# Scope: PROJECT — PostToolUse on Bash
#
# Fires after every Bash invocation. Exits in <50ms unless the command was a
# successful `gh pr merge`, in which case it triggers the
# `update-cc-progress.ps1` worker so `<planning-docs>/_cc-progress/state-of-main.md`
# stays in lock-step with `origin/main` after every CC-driven merge.
#
# Wired in `.claude/settings.json` under `PostToolUse[matcher=Bash].hooks`.
#
# Stdin: JSON envelope per Claude Code's hook contract (see anthropic docs).
# Stdout: nothing (informational only — never blocks the tool result).
# Exit: always 0 (best-effort; never blocks the agent).

$ErrorActionPreference = "Stop"

# Read the JSON envelope from stdin. Bail early if we can't parse.
try {
  $raw = [Console]::In.ReadToEnd()
  if (-not $raw) { exit 0 }
  $payload = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
  # Don't surface parse errors — this hook is fire-and-forget.
  exit 0
}

# Only act on Bash tool calls.
if ($payload.tool_name -ne "Bash") { exit 0 }

# Inspect the command. We're looking for `gh pr merge ...` anywhere in the
# command — CC routinely composes invocations like `cd repo && gh pr merge ...`
# or `git fetch && gh pr merge ...`, so anchoring at line start would silently
# miss the common case. The negative lookbehind blocks the most obvious false
# positive (`echo "gh pr merge"`); other quoting forms remain edge cases.
$command = $payload.tool_input.command
if (-not $command) { exit 0 }

if ($command -notmatch '(?<![''"`])\bgh(\.exe)?\s+pr\s+merge\b') { exit 0 }

# Determine whether the merge actually succeeded. Prefer the structured
# exit_code field when Claude Code provides it; fall back to interrupted
# + stderr-substring heuristics.
$response = $payload.tool_response
if ($response -and $response.interrupted) { exit 0 }

if ($response -and $null -ne $response.exit_code -and $response.exit_code -ne 0) {
  [Console]::Error.WriteLine("[post-merge-cc-progress] gh pr merge exited non-zero ($($response.exit_code)); skipping digest update")
  exit 0
}

# Belt-and-suspenders: even on exit 0, GitHub sometimes returns a soft failure
# message on stderr (e.g. "is not mergeable: the merge commit cannot be cleanly
# created"). Use a tight phrase list to avoid false positives on unrelated
# stderr noise containing the word "error".
if ($response -and $response.stderr) {
  $stderr = "$($response.stderr)"
  if ($stderr -match '(?i)not mergeable|merge conflict|merge commit cannot be cleanly created') {
    [Console]::Error.WriteLine("[post-merge-cc-progress] merge looks unsuccessful (stderr); skipping digest update")
    exit 0
  }
}

# All checks passed — fire the worker.
$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) {
  [Console]::Error.WriteLine("[post-merge-cc-progress] CLAUDE_PROJECT_DIR not set; skipping")
  exit 0
}

$worker = Join-Path $projectDir ".claude/hooks/update-cc-progress.ps1"
if (-not (Test-Path $worker)) {
  [Console]::Error.WriteLine("[post-merge-cc-progress] worker missing at $worker; skipping")
  exit 0
}

$env:CC_PROGRESS_SOURCE = "post-merge hook"
& pwsh -NoProfile -File $worker | Out-Null

exit 0
