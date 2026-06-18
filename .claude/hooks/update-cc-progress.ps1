# Scope: PROJECT — workhorse for _cc-progress/state-of-main.md
#
# Regenerates `<planning-docs>/_cc-progress/state-of-main.md` from current
# origin/main. Idempotent: if main hasn't moved since the on-disk digest's
# stamped SHA, exits as a no-op. Safe to invoke from any worktree.
#
# Concurrency model:
#   - Acquires an atomic mkdir lock at `<planning-docs>/_cc-progress/.lock/`.
#     NTFS mkdir is atomic; concurrent writers race for the same dir and only
#     one succeeds. Losers retry with 500ms backoff for up to 30s.
#   - Stale locks (>5 min mtime) are stolen, on the assumption that the
#     previous holder crashed mid-write.
#   - Content is a deterministic function of origin/main HEAD, so concurrent
#     writers converge to the same output. Last-writer-wins is safe.
#   - Atomic write: Set-Content to a temp file, then Move-Item -Force to the
#     final path. NTFS rename across the same filesystem is atomic.
#
# Inputs (all optional):
#   $env:CC_PROGRESS_PLANNING_DOCS — REQUIRED on first install. Absolute path
#                                    of the project's planning workspace (the
#                                    sibling folder Cowork writes into, which
#                                    contains _cc-progress/). init-project.ps1
#                                    wires this into .claude/settings.json's
#                                    env block. Falls back to a sibling guess
#                                    if unset.
#   $env:CC_PROGRESS_SOURCE        — string written into the digest's frontmatter
#                                    (default: "manual")
#
# Exit codes: always 0 (best-effort; never blocks the caller).

$ErrorActionPreference = "Stop"
$here = "update-cc-progress"

function Write-Log($msg) { [Console]::Error.WriteLine("[$here] $msg") }

# --- Resolve paths (with traversal-safety guards) ----------------------------

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) {
  Write-Log "CLAUDE_PROJECT_DIR not set; trying current directory"
  $projectDir = (Get-Location).Path
}

# Normalize and assert projectDir actually looks like a repo root.
try {
  $projectDir = (Resolve-Path -LiteralPath $projectDir -ErrorAction Stop).Path
} catch {
  Write-Log "CLAUDE_PROJECT_DIR ('$projectDir') does not resolve; skipping"
  exit 0
}
if (-not (Test-Path (Join-Path $projectDir ".git"))) {
  Write-Log "CLAUDE_PROJECT_DIR ('$projectDir') is not a git repo root; refusing to operate"
  exit 0
}

$planningDocs = $env:CC_PROGRESS_PLANNING_DOCS
if (-not $planningDocs) {
  # Fallback guess: sibling folder named "<project>-planning" next to the impl repo.
  $projectName = Split-Path -Leaf $projectDir
  $planningDocs = Join-Path (Split-Path -Parent $projectDir) "$projectName-planning"
}
try {
  $planningDocs = (Resolve-Path -LiteralPath $planningDocs -ErrorAction Stop).Path
} catch {
  Write-Log "planning docs path ('$planningDocs') does not resolve; skipping"
  exit 0
}

# Defense against env-var-driven write-anywhere: planning docs must live under
# the project's parent directory. Override only changes WHICH sibling of the
# project — never to an unrelated tree (no `C:\Windows\Temp\...` redirects).
$projectParent = (Resolve-Path -LiteralPath (Split-Path -Parent $projectDir)).Path
if (-not $planningDocs.StartsWith($projectParent, [StringComparison]::OrdinalIgnoreCase)) {
  Write-Log "planning docs ('$planningDocs') is not under project parent ('$projectParent'); refusing"
  exit 0
}

if (-not (Test-Path $planningDocs)) {
  Write-Log "planning docs not found at '$planningDocs'; skipping"
  exit 0
}

$progressDir = Join-Path $planningDocs "_cc-progress"
if (-not (Test-Path $progressDir)) {
  Write-Log "'_cc-progress/' not found at '$progressDir'; skipping (run the bootstrap once first)"
  exit 0
}

$source = if ($env:CC_PROGRESS_SOURCE) { $env:CC_PROGRESS_SOURCE } else { "manual" }
$targetFile = Join-Path $progressDir "state-of-main.md"
$lockDir = Join-Path $progressDir ".lock"

# --- Lock acquisition --------------------------------------------------------

function Try-AcquireLock {
  try {
    New-Item -ItemType Directory -Path $lockDir -ErrorAction Stop | Out-Null
    return $true
  } catch {
    return $false
  }
}

$acquired = $false
$deadline = (Get-Date).AddSeconds(30)
while (-not $acquired -and (Get-Date) -lt $deadline) {
  if (Try-AcquireLock) {
    $acquired = $true
    break
  }
  # Stale-lock detection: steal if older than 5 minutes
  if (Test-Path $lockDir) {
    $age = (Get-Date) - (Get-Item $lockDir).LastWriteTime
    if ($age.TotalMinutes -gt 5) {
      Write-Log "stealing stale lock (age: $([int]$age.TotalSeconds)s)"
      try { Remove-Item -Recurse -Force $lockDir -ErrorAction SilentlyContinue } catch {}
      if (Try-AcquireLock) { $acquired = $true; break }
    }
  }
  Start-Sleep -Milliseconds 500
}

if (-not $acquired) {
  Write-Log "could not acquire lock after 30s; another updater is running, skipping"
  exit 0
}

# --- Main work, guarded by try/finally for lock cleanup ----------------------

try {
  # Sweep orphan temp files from prior crashed runs. Safe inside the lock.
  Get-ChildItem -Path $progressDir -Filter "state-of-main.md.tmp.*" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

  # Fetch latest origin/main without touching the worktree.
  Push-Location $projectDir
  try {
    & git fetch --quiet origin main 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
      Write-Log "git fetch failed (exit $LASTEXITCODE); skipping"
      exit 0
    }
    $currentSha = (& git rev-parse origin/main).Trim()
  } finally {
    Pop-Location
  }

  if (-not $currentSha -or $currentSha.Length -lt 40) {
    Write-Log "could not resolve origin/main SHA; skipping"
    exit 0
  }

  # Compare against on-disk digest's SHA.
  $priorSha = $null
  if (Test-Path $targetFile) {
    $frontmatter = Get-Content $targetFile -First 10
    foreach ($line in $frontmatter) {
      if ($line -match '^main_sha:\s*([0-9a-f]{40})\s*$') {
        $priorSha = $matches[1]
        break
      }
    }
  }

  if ($priorSha -eq $currentSha) {
    Write-Log "no change (main still at $($currentSha.Substring(0,7)))"
    exit 0
  }

  $priorShort = if ($priorSha) { $priorSha.Substring(0, 7) } else { "(none)" }
  Write-Log "main moved: $priorShort -> $($currentSha.Substring(0,7))"

  # --- Gather data for the digest ------------------------------------------

  Push-Location $projectDir
  try {
    $shortSha = $currentSha.Substring(0, 7)
    $headSubject = (& git log -1 --format="%s" $currentSha).Trim()
    $headDate = (& git log -1 --format="%cI" $currentSha).Trim()

    # Recent merge commits on main (last 20).
    $recentMerges = & git log --merges --max-count=20 --format="%h`t%cI`t%s" $currentSha

    # Merges since prior digest (if we have a prior SHA + it's an ancestor).
    $newMerges = @()
    $newMergesNote = ""
    if ($priorSha) {
      $ancestorCheck = & git merge-base --is-ancestor $priorSha $currentSha 2>&1
      if ($LASTEXITCODE -eq 0) {
        $newMerges = & git log --merges "$priorSha..$currentSha" --format="%h`t%cI`t%s"
      } else {
        $newMergesNote = "(prior SHA $($priorSha.Substring(0,7)) is not an ancestor of current main — possible force-push or branch reset; showing last 20 merges only)"
      }
    } else {
      $newMergesNote = "(no prior digest; showing last 20 merges as initial baseline)"
    }

    # Files changed since prior digest (if available).
    $diffStat = ""
    if ($priorSha -and -not $newMergesNote) {
      $diffStat = (& git diff --stat "$priorSha..$currentSha" 2>&1) -join "`n"
    }
  } finally {
    Pop-Location
  }

  # --- Compose the digest --------------------------------------------------

  $generatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

  function Format-MergeTable($rows) {
    if (-not $rows -or $rows.Count -eq 0) {
      return "_(none)_"
    }
    $out = @("| Short SHA | Date (UTC) | Subject |", "|---|---|---|")
    foreach ($row in $rows) {
      $parts = $row -split "`t", 3
      if ($parts.Count -eq 3) {
        # Escape `|` inside subjects so they don't break the table.
        $subj = $parts[2] -replace '\|', '\|'
        $out += "| ``$($parts[0])`` | $($parts[1]) | $subj |"
      }
    }
    return ($out -join "`n")
  }

  $recentTable = Format-MergeTable $recentMerges
  $newTable = if ($newMergesNote) { $newMergesNote } else { Format-MergeTable $newMerges }

  $diffBlock = if ($diffStat) { "``````" + "`n" + $diffStat.Trim() + "`n" + "``````" } else { "_(no diff available — see 'last 20 merges' table above)_" }

  $content = @"
---
main_sha: $currentSha
short_sha: $shortSha
generated_at: $generatedAt
generated_by: $source
prior_sha: $(if ($priorSha) { $priorSha } else { "(none)" })
---

# State of ``main`` — auto-generated

**HEAD:** ``$shortSha`` — $headSubject
**HEAD committed:** $headDate
**Digest generated:** $generatedAt by ``$source``

This file is regenerated by ``.claude/hooks/update-cc-progress.ps1`` on every successful
``gh pr merge`` and on session start (safety net). For human-authored interpretation of
what these merges mean for the board, see siblings ``board-deltas.md`` and
``decisions-and-gotchas.md`` (refreshed manually via ``@cc-progress-writer``).

## New merges since prior digest

$newTable

## Last 20 merges on ``main``

$recentTable

## Files changed since prior digest

$diffBlock

## Reproduction

``````sh
cd "$projectDir"
git fetch origin main
git rev-parse origin/main
``````

"@

  # --- Atomic write --------------------------------------------------------
  # UTF8NoBOM: pwsh 7's `-Encoding UTF8` is BOM-less, but Windows PowerShell 5.1
  # writes a BOM with the same flag — that would break the SHA-extraction regex
  # on the next read. Be explicit to survive cross-shell invocation.

  $tempFile = "$targetFile.tmp.$PID"
  $content | Set-Content -Path $tempFile -Encoding UTF8NoBOM -NoNewline
  Move-Item -Force -Path $tempFile -Destination $targetFile

  Write-Log "wrote $targetFile (main @ $shortSha)"
} finally {
  if ($acquired) {
    try { Remove-Item -Recurse -Force $lockDir -ErrorAction SilentlyContinue } catch {}
  }
}

exit 0
