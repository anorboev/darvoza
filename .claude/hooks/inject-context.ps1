# Scope: PROJECT — UserPromptSubmit
# Injects per-session context (branch + timestamp) as additionalContext.
# exit 0 always.

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { exit 0 }

Push-Location $projectDir
try {
  $branch = ""
  try {
    $rawBranch = & git rev-parse --abbrev-ref HEAD 2>$null
    if ($rawBranch) { $branch = $rawBranch.Trim() }
  } catch {}
  if (-not $branch) { $branch = "(unknown)" }

  # Strip characters that could break out of the surrounding <session-context> tag,
  # or be interpreted as markup by a downstream parser.
  $branch = $branch -replace '[<>&"]', '_'

  $context = @{
    branch = $branch
    open_prs = "(check via gh pr list)"
    timestamp_utc = (Get-Date).ToUniversalTime().ToString("o")
  } | ConvertTo-Json -Compress

  Write-Output "<session-context>$context</session-context>"
} finally {
  Pop-Location
}
exit 0
