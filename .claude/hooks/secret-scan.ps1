# Scope: PROJECT - PreToolUse:Write|Edit|MultiEdit
# Scans file content about to be written/edited for secret-shape patterns.
# exit 2 = hard block; exit 0 = allow.
#
# Fail-closed: parse errors result in exit 2 (the harness sees a block, not
# a free pass). Patterns are loaded from patterns.json next to this script;
# optional patterns-local.json is merged in for project-specific extensions
# (typically gitignored so per-project shape doesn't leak to public toolkit).

try {
  $input_json = [Console]::In.ReadToEnd()
  if (-not $input_json) { exit 0 }
  $payload = $input_json | ConvertFrom-Json
} catch {
  [Console]::Error.WriteLine("BLOCKED by secret-scan: malformed hook payload ($($_.Exception.Message))")
  exit 2
}

# --- Collect text content from any of the supported tool shapes -------------

$text = ""
if ($payload.tool_input.content) { $text += "`n" + $payload.tool_input.content }
if ($payload.tool_input.new_string) { $text += "`n" + $payload.tool_input.new_string }
if ($payload.tool_input.edits) {
  $editsValue = $payload.tool_input.edits
  if ($editsValue) {
    foreach ($e in $editsValue) {
      if ($e -and $e.new_string) { $text += "`n" + $e.new_string }
    }
  }
}
if (-not $text) { exit 0 }

# Allow .env.example BY BASENAME, not suffix - prevents "evil.env.example.ts" bypass.
$filePath = $payload.tool_input.file_path
if ($filePath) {
  $basename = [System.IO.Path]::GetFileName($filePath)
  if ($basename -ieq ".env.example") { exit 0 }
}

# --- Load patterns ----------------------------------------------------------

function Load-Patterns($file) {
  if (-not (Test-Path $file)) { return $null }
  try {
    return (Get-Content -Raw $file | ConvertFrom-Json)
  } catch {
    [Console]::Error.WriteLine("BLOCKED by secret-scan: $file is malformed JSON ($($_.Exception.Message))")
    exit 2
  }
}

$patternsFile = Join-Path $PSScriptRoot "patterns.json"
$localFile    = Join-Path $PSScriptRoot "patterns-local.json"

$primary = Load-Patterns $patternsFile
if (-not $primary) {
  [Console]::Error.WriteLine("BLOCKED by secret-scan: patterns.json not found next to script at $patternsFile")
  exit 2
}

$blockPatterns = @($primary.block)
$warnPatterns  = @($primary.warn)

$local = Load-Patterns $localFile
if ($local) {
  if ($local.block) { $blockPatterns += @($local.block) }
  if ($local.warn)  { $warnPatterns  += @($local.warn) }
}

# --- Apply patterns ---------------------------------------------------------

foreach ($p in $blockPatterns) {
  if (-not $p.pattern) { continue }
  if ($text -match $p.pattern) {
    [Console]::Error.WriteLine("BLOCKED by secret-scan: $($p.name) detected.")
    [Console]::Error.WriteLine("If this is a placeholder/example, mark it with a comment like '# example only, not a real secret' and rerun. To add a permanent exception, edit patterns-local.json.")
    exit 2
  }
}

foreach ($p in $warnPatterns) {
  if (-not $p.pattern) { continue }
  if ($text -match $p.pattern) {
    [Console]::Error.WriteLine("[warn] secret-scan: $($p.name) detected. These are safe to commit but worth a glance.")
  }
}

exit 0
