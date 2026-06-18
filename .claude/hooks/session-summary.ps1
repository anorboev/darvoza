# Scope: PROJECT (darvoza) — SessionStart
# Surfaces the most recent handoff doc (if any) so a new session inherits context.
# Also fires the cc-progress worker as a safety net — catches PR merges done via
# the GitHub web UI (or by another agent on this machine) that the post-merge
# hook missed because no Bash `gh pr merge` ran. Worker is concurrency-safe.
# Output is markdown on stdout; exit 0 always.

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { exit 0 }

# --- cc-progress safety net (background, never blocks session start) --------

$ccProgressWorker = Join-Path $projectDir ".claude/hooks/update-cc-progress.ps1"
if (Test-Path $ccProgressWorker) {
  try {
    # Best-effort: don't let a slow git fetch delay session bootstrap. 10s cap.
    # Pass the CC_PROGRESS_SOURCE label through -ArgumentList — Start-Job
    # spawns a child process that does NOT inherit our $env: mutations.
    $job = Start-Job -ScriptBlock {
      param($worker, $source)
      $env:CC_PROGRESS_SOURCE = $source
      & pwsh -NoProfile -File $worker | Out-Null
    } -ArgumentList $ccProgressWorker, "session-start safety net"
    $null = Wait-Job -Job $job -Timeout 10
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
  } catch {
    # Silent — handoff surface below is the actual SessionStart deliverable.
  }
}

$handoffDir = Join-Path $projectDir "docs/handoffs"
if (-not (Test-Path $handoffDir)) { exit 0 }

$latest = Get-ChildItem -Path $handoffDir -Filter "*.md" -File |
  Sort-Object Name -Descending |
  Select-Object -First 1

if (-not $latest) { exit 0 }

Write-Output ""
Write-Output "## Last session handoff — $($latest.Name)"
Write-Output ""
Get-Content $latest.FullName | Select-Object -First 40
Write-Output ""
Write-Output "_(truncated to first 40 lines — full file: docs/handoffs/$($latest.Name))_"
exit 0
