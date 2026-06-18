# Scope: PROJECT
# OS-aware dispatcher. Resolves hook scripts to PowerShell on Windows, bash elsewhere.
# Currently unwired (settings.json invokes hook files directly) — kept for future use.

param(
  [Parameter(Mandatory = $true)][string]$Script
)

# Defense in depth: only accept simple identifiers — no traversal, no path separators.
if ($Script -notmatch '^[A-Za-z0-9_-]+$') {
  [Console]::Error.WriteLine("[_dispatch] invalid script name: '$Script'")
  exit 1
}

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) {
  [Console]::Error.WriteLine("[_dispatch] CLAUDE_PROJECT_DIR not set; aborting")
  exit 1
}

$psScript = Join-Path $projectDir ".claude/hooks/$Script.ps1"
$jsScript = Join-Path $projectDir ".claude/hooks/$Script.js"

if ($IsWindows -or $env:OS -eq "Windows_NT") {
  if (Test-Path $psScript) {
    & pwsh -NoProfile -File $psScript
    exit $LASTEXITCODE
  }
  if (Test-Path $jsScript) {
    & node $jsScript
    exit $LASTEXITCODE
  }
} else {
  if (Test-Path $jsScript) {
    & node $jsScript
    exit $LASTEXITCODE
  }
  if (Test-Path $psScript) {
    & pwsh -NoProfile -File $psScript
    exit $LASTEXITCODE
  }
}

[Console]::Error.WriteLine("[_dispatch] no script found for '$Script' under .claude/hooks/")
exit 1
