# Scope: PROJECT — Stop
# On session stop, writes a handoff document under docs/handoffs/<UTC-ISO>.md.
# exit 0 always.
#
# Security: uses a single-quoted here-string + .Replace() to prevent PowerShell
# expansion of attacker-controllable strings (commit messages, branch names,
# filenames in `git status` can legally contain $(...) which would otherwise execute).

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { exit 0 }

$handoffDir = Join-Path $projectDir "docs/handoffs"
if (-not (Test-Path $handoffDir)) { New-Item -ItemType Directory -Path $handoffDir | Out-Null }

$ts = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$file = Join-Path $handoffDir "$ts.md"

Push-Location $projectDir
try {
  $branch = ""
  try { $branch = (& git rev-parse --abbrev-ref HEAD 2>$null).Trim() } catch {}
  if (-not $branch) { $branch = "(unknown)" }

  $log = (& git log --oneline -n 5 2>$null) -join "`n"
  if (-not $log) { $log = "(no commits yet)" }

  $dirty = (& git status --short 2>$null) -join "`n"
  if (-not $dirty) { $dirty = "(clean)" }

  # Single-quoted here-string: NO expansion. Use {0}/{1}/{2}/{3} placeholders + -f.
  $template = @'
# Session handoff — {0} UTC

**Branch:** {1}

**Last 5 commits:**

```
{2}
```

**Dirty files:**

```
{3}
```

**Notes:**

(Stub handoff. Invoke the `handoff-rich` skill for a richer dump: open PRs, failing tests, outbox depth, last user intent.)
'@

  $content = $template -f $ts, $branch, $log, $dirty

  Set-Content -Path $file -Value $content -Encoding utf8
} finally {
  Pop-Location
}
exit 0
