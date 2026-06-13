param(
    [int]    $GoalsPerBatch = 60,
    [string] $GenModel      = "qwen2.5-coder:7b",
    [string] $LocalHost     = "http://localhost:11434",
    [string] $Workspace     = "\\HARDCORERIK\F$\Ai\OrchestratorIDE",
    [string] $Prefix        = ""
)
$ErrorActionPreference = "Stop"

if (-not $Prefix) { $Prefix = "HCSCOUT" + (Get-Date -Format "yyMMdd_HHmm") }

$outDir = Join-Path $Workspace "training_pit"
$outFile = Join-Path $outDir "batch_${Prefix}_goals.psv"

Write-Host "`n[SCOUT] Generating $GoalsPerBatch goals via $GenModel at $LocalHost"
Write-Host "[SCOUT] Output: $outFile"

# Build the prompt for goal authoring
$systemPrompt = @"
You are an expert goal author for ORC ACADEMY, a multi-agent software development system.
Your job is to write realistic, diverse software engineering goals that the system's boss model
will decompose into task plans.

Rules:
- Each goal must be a realistic software engineering request
- Goals should span: C#/WPF desktop apps, PowerShell automation, data processing, API integration,
  embedded/IoT, testing, documentation, refactoring
- Each goal must be specific enough that a dev team could act on it immediately
- Goals should vary in complexity: simple (1-2 tasks), medium (3-4 tasks)
- Do NOT write goals that require internet access, credentials, or external services
- Format: one goal per line, no numbering, no markdown
"@

$userPrompt = "Write exactly $GoalsPerBatch software engineering goals. One per line, no numbering, no markdown bullets."

$body = @{
    model = $GenModel
    messages = @(
        @{ role = "system"; content = $systemPrompt }
        @{ role = "user";   content = $userPrompt   }
    )
    stream = $false
    options = @{ temperature = 0.9; num_predict = 4096 }
} | ConvertTo-Json -Depth 10

Write-Host "[SCOUT] Calling Ollama..."
try {
    $resp = Invoke-RestMethod -Uri "$LocalHost/api/chat" -Method POST -Body $body `
        -ContentType "application/json" -TimeoutSec 120
    $raw = $resp.message.content
} catch {
    Write-Error "Ollama call failed: $_"
    exit 1
}

# Parse lines into goals, deduplicate
$goals = $raw -split "`n" |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -ne "" -and $_.Length -gt 20 -and -not $_.StartsWith("#") } |
    Select-Object -Unique

Write-Host "[SCOUT] Got $($goals.Count) unique goals"

# Load existing goals from all batch files for dedup
$allExisting = @()
Get-ChildItem (Join-Path $outDir "batch_*_goals.psv") -ErrorAction SilentlyContinue | ForEach-Object {
    $allExisting += Get-Content $_.FullName
}

$novel = $goals | Where-Object { $_ -notin $allExisting }
Write-Host "[SCOUT] $($novel.Count) novel goals (after dedup against $($allExisting.Count) existing)"

if ($novel.Count -eq 0) {
    Write-Warning "[SCOUT] No novel goals generated — novelty may be exhausted."
    exit 0
}

# Write PSV (pipe-separated: prefix|goal)
$lines = $novel | ForEach-Object { "${Prefix}|$_" }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$lines | Set-Content -Path $outFile -Encoding UTF8

Write-Host "[SCOUT] Saved $($novel.Count) goals -> $outFile"

# Summary
Write-Host "`n[SCOUT] Done. First 3 goals:"
$novel | Select-Object -First 3 | ForEach-Object { Write-Host "  - $_" }
