# Scope: PROJECT — PreToolUse:Bash
# Policy: CC may push feature branches. Hook blocks only patterns that are
# unambiguously destructive or that bypass commit/lint hooks:
#   - `git push --force` / `--force-with-lease` (any target)
#   - `--no-verify` (bypasses lefthook commit checks)
#
# Direct-push-to-main protection is GitHub's responsibility (branch protection
# rules). Once the repo has those enabled, even a CC push to main will be
# refused by the server.
#
# Fail-closed on parse errors. exit 2 = hard block; exit 0 = allow.

try {
  $input_json = [Console]::In.ReadToEnd()
  if (-not $input_json) { exit 0 }
  $payload = $input_json | ConvertFrom-Json
} catch {
  [Console]::Error.WriteLine("BLOCKED by block-bad-git-push: malformed hook payload ($($_.Exception.Message))")
  exit 2
}

$cmd = $payload.tool_input.command
if (-not $cmd) { exit 0 }

$tokens = @($cmd -split '\s+' | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim("'""") })

$gitIdx = -1
for ($i = 0; $i -lt $tokens.Count; $i++) {
  if ($tokens[$i] -eq 'git') { $gitIdx = $i; break }
}
if ($gitIdx -lt 0) { exit 0 }

# Locate the `push` subcommand (skipping git global flags).
$pushIdx = -1
for ($i = $gitIdx + 1; $i -lt $tokens.Count; $i++) {
  $t = $tokens[$i]
  if ($t -eq 'push') { $pushIdx = $i; break }
  if ($t -notmatch '^(--?|-c$|--git-dir|--work-tree|--namespace|--exec-path)') { break }
}
if ($pushIdx -lt 0) { exit 0 }

$pushArgs = $tokens[($pushIdx + 1)..($tokens.Count - 1)]

foreach ($a in $pushArgs) {
  if ($a -eq '-f' -or $a -eq '--force' -or $a -like '--force-with-lease*') {
    [Console]::Error.WriteLine("BLOCKED: force-push is human-only (refused by hook block-bad-git-push). If you truly need it, run from your own terminal.")
    exit 2
  }
  if ($a -eq '--no-verify') {
    [Console]::Error.WriteLine("BLOCKED: --no-verify bypasses lefthook (refused by hook block-bad-git-push).")
    exit 2
  }
}

exit 0
