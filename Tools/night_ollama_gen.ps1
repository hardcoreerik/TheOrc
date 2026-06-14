<#
night_ollama_gen.ps1  zero-dependency overnight synthetic gold generator.

Generates boss-decomposition training examples by calling a local Ollama model
directly over HTTP (Invoke-RestMethod). No Python, no SDKs, no venv  only
Windows PowerShell 5.1+ and a running Ollama. Built for machines where the
Python toolchain is broken but the GPU + Ollama are healthy (e.g. HARDCOREPC).

Each cycle asks the model to emit a batch of N examples as JSON lines, validates
and de-dupes them, wraps each into the canonical training schema, and appends to
the output JSONL. Loops until a deadline (default 06:00) or a stop file appears.

  powershell -NoProfile -ExecutionPolicy Bypass -File night_ollama_gen.ps1 `
      -Seed  "F:\Ai\TheOrchestrator\training_pit\datasets\seed.jsonl" `
      -Out   "F:\Ai\TheOrchestrator\training_pit\datasets\ollama_gold.work.jsonl" `
      -Model "qwen2.5-coder:7b" -Until "06:00"

Stop cleanly:  New-Item <Out-dir>\NIGHT_GEN_STOP
#>
param(
    [string]$Seed,
    [string]$Out,
    [string]$Model      = "qwen2.5-coder:7b",
    [string]$OllamaHost = "http://localhost:11434",
    [string]$Until      = "06:00",
    [double]$Hours      = 0,
    [int]$PerBatch      = 8,
    [int]$NumCtx        = 8192,
    [double]$Temp       = 0.9,
    [string]$StopFile   = ""
)

$ErrorActionPreference = "Stop"

# Disable proxy auto-detection. In a non-interactive / session-0 scheduled-task
# context, WebRequest's system-proxy probe can stall Invoke-RestMethod even for
# localhost, hanging the whole run. Forcing no proxy keeps localhost direct.
try { [System.Net.WebRequest]::DefaultWebProxy = $null } catch { }

if (-not $Seed -or -not (Test-Path -LiteralPath $Seed)) { Write-Error "Seed JSONL not found: $Seed"; exit 1 }
if (-not $Out) { Write-Error "-Out path is required"; exit 1 }
$outDir = Split-Path $Out -Parent
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if (-not $StopFile) { $StopFile = Join-Path $outDir "NIGHT_GEN_STOP" }
$progressFile = [System.IO.Path]::ChangeExtension($Out, ".progress.json")
$logFile      = [System.IO.Path]::ChangeExtension($Out, ".gen.log")

# -- Deadline ----------------------------------------------------------------
if ($Hours -gt 0) {
    $deadline = (Get-Date).AddHours($Hours)
} else {
    $deadline = Get-Date "$(Get-Date -Format yyyy-MM-dd) $Until"
    if ($deadline -le (Get-Date)) { $deadline = $deadline.AddDays(1) }
}

function Log($msg) {
    $line = "$(Get-Date -Format HH:mm:ss)  $msg"
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}

if (Test-Path -LiteralPath $StopFile) { Log "STOP file already present  refusing to start. Remove $StopFile"; exit 1 }

# -- Load canonical system prompt from the seed's first record ----------------
$firstLine = Get-Content -LiteralPath $Seed -TotalCount 1
$systemPrompt = ($firstLine | ConvertFrom-Json).messages[0].content
if (-not $systemPrompt -or $systemPrompt.Length -lt 50) { Write-Error "Seed system prompt looks wrong (len=$($systemPrompt.Length))"; exit 1 }

# -- Diversity rotation ------------------------------------------------------
$languages = @("Python","C#","JavaScript/TypeScript","TypeScript (React)","SQL","Rust","Go","Java","PowerShell","Bash","HTML/CSS")
$taskTypes = @("bugfix","refactor","tests","feature","integration","docs","ui")
$validRoles = @("RESEARCHER","CODER","UIDEVELOPER","TESTER")
$noUiLangs  = @("SQL","Bash","PowerShell")

function NormGoal($g) { return ($g -replace '\s+', ' ').Trim().ToLower() }

# -- Resume: load already-seen goals so we never duplicate -------------------
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$written = 0
if (Test-Path -LiteralPath $Out) {
    foreach ($l in [System.IO.File]::ReadLines($Out)) {
        if ($l.Trim().Length -eq 0) { continue }
        try {
            $obj = $l | ConvertFrom-Json
            $g = $obj.messages[1].content
            if ($g -like "Goal: *") { $g = $g.Substring(6) }
            [void]$seen.Add((NormGoal $g))
            $written++
        } catch { }
    }
    Log "RESUME: $written existing examples loaded"
}

function Build-Prompt($lang, $task) {
    if ($noUiLangs -contains $lang) { $uiNote = "Do NOT include a UIDEVELOPER task (non-UI language)." } else { $uiNote = "" }
    $researcherNote = "Most examples should have NO RESEARCHER task; only add one for genuinely unfamiliar third-party APIs."
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("You are generating GOLD training data for a software-project planning model.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Produce exactly $PerBatch DISTINCT, realistic developer goals in the domain of $lang, themed as '$task' tasks.")
    [void]$sb.AppendLine("For EACH goal, decompose it into a short plan and 1-4 concrete tasks.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Rules:")
    [void]$sb.AppendLine("- Each task has: role (one of RESEARCHER, CODER, UIDEVELOPER, TESTER), priority (1=highest to 5), a short title, and a 1-2 sentence concrete description.")
    [void]$sb.AppendLine("- Every coding goal MUST include at least one TESTER task. $researcherNote $uiNote")
    [void]$sb.AppendLine("- Goals should sound like a real developer wrote them (casual, sometimes terse). Vary them widely.")
    [void]$sb.AppendLine("- Theme '$task' means: bugfix=fix a defect, refactor=restructure without behavior change, tests=add coverage, feature=new capability, integration=wire two systems, docs=documentation, ui=user interface work.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Output ONE JSON object PER LINE (JSONL). No markdown, no commentary, no surrounding array.")
    [void]$sb.AppendLine("Each line must look like:")
    [void]$sb.AppendLine('{"goal":"<text>","plan":"<one sentence>","tasks":[{"role":"CODER","priority":1,"title":"<t>","description":"<d>"}]}')
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Emit $PerBatch lines now.")
    return $sb.ToString()
}

function Invoke-Ollama($userPrompt) {
    $body = @{
        model    = $Model
        messages = @(
            @{ role = "system"; content = $systemPrompt },
            @{ role = "user";   content = $userPrompt }
        )
        stream  = $false
        options = @{ temperature = $Temp; num_ctx = $NumCtx }
    } | ConvertTo-Json -Depth 8 -Compress

    # Send the body as explicit UTF-8 bytes. Windows PowerShell 5.1's
    # Invoke-RestMethod otherwise encodes a string body as Latin1, corrupting
    # any non-ASCII in the (captured) system prompt -> Ollama returns HTTP 400.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $resp = Invoke-RestMethod -Uri "$OllamaHost/api/chat" -Method Post -Body $bytes `
        -ContentType "application/json; charset=utf-8" -TimeoutSec 300
    return $resp.message.content
}

# Extract every balanced top-level {...} block from arbitrary text. Robust to
# pretty-printed multi-line JSON, JSON arrays, code fences, and prose preamble 
# all of which small models emit unpredictably. Respects strings and escapes.
function Extract-JsonObjects($text) {
    $blocks = @()
    $depth = 0; $start = -1; $inStr = $false; $esc = $false
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($inStr) {
            if ($esc) { $esc = $false }
            elseif ($c -eq '\') { $esc = $true }
            elseif ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq '{') { if ($depth -eq 0) { $start = $i }; $depth++ }
        elseif ($c -eq '}') {
            $depth--
            if ($depth -eq 0 -and $start -ge 0) {
                $blocks += $text.Substring($start, $i - $start + 1)
                $start = -1
            }
            if ($depth -lt 0) { $depth = 0 }
        }
    }
    return $blocks
}

function Parse-Examples($raw, $lang, $task) {
    $results = @()
    foreach ($block in (Extract-JsonObjects $raw)) {
        try { $obj = $block | ConvertFrom-Json } catch { continue }
        # Skip nested task objects that match standalone (they lack 'goal')
        if (-not $obj.goal -or -not $obj.plan -or -not $obj.tasks) { continue }
        if ($obj.tasks.Count -lt 1) { continue }
        $okTasks = @()
        foreach ($t in $obj.tasks) {
            if ($validRoles -notcontains $t.role) { continue }
            if (-not $t.title -or -not $t.description) { continue }
            $pri = 3; if ($t.priority) { try { $pri = [int]$t.priority } catch { $pri = 3 } }
            if ($pri -lt 1) { $pri = 1 }; if ($pri -gt 5) { $pri = 5 }
            $okTasks += [ordered]@{ role = $t.role; priority = $pri; title = "$($t.title)"; description = "$($t.description)" }
        }
        if ($okTasks.Count -lt 1) { continue }
        $results += [pscustomobject]@{ goal = "$($obj.goal)"; plan = "$($obj.plan)"; tasks = $okTasks; lang = $lang; task = $task }
    }
    return $results
}

function Wrap-Example($ex) {
    $assistant = @{ plan = $ex.plan; tasks = $ex.tasks } | ConvertTo-Json -Depth 8 -Compress
    $obj = [ordered]@{
        messages = @(
            [ordered]@{ role = "system";    content = $systemPrompt }
            [ordered]@{ role = "user";      content = "Goal: $($ex.goal)" }
            [ordered]@{ role = "assistant"; content = $assistant }
        )
        metadata = [ordered]@{
            category                = "boss_planning"
            task_type               = $ex.task
            source                  = "ollama_synthetic"
            quality                 = "gold"
            contains_sensitive_data = $false
            base_model_target       = "theorc-boss:gemma4"
            created_by              = "night_ollama_gen.ps1"
            language                = $ex.lang
            style                   = "multi_sentence"
            model                   = $Model
            host                    = $env:COMPUTERNAME
        }
    }
    return ($obj | ConvertTo-Json -Depth 10 -Compress)
}

# -- Main loop ---------------------------------------------------------------
Log "NIGHT OLLAMA GEN begins  model=$Model  until=$($deadline.ToString('ddd HH:mm'))  seed-sys-len=$($systemPrompt.Length)"
Log "out=$Out  stop=$StopFile"

$cycle = 0
$li = 0; $ti = 0
$t0 = Get-Date

while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $StopFile) { Log "stop file detected  ending"; break }
    $cycle++
    $lang = $languages[$li % $languages.Count]; $li++
    $task = $taskTypes[$ti % $taskTypes.Count];  $ti++
    if ($noUiLangs -contains $lang -and $task -eq "ui") { $task = "feature" }

    $kept = 0
    try {
        $raw = Invoke-Ollama (Build-Prompt $lang $task)
        $examples = Parse-Examples $raw $lang $task
        foreach ($ex in $examples) {
            $ng = NormGoal $ex.goal
            if ($seen.Contains($ng)) { continue }
            [void]$seen.Add($ng)
            Add-Content -LiteralPath $Out -Value (Wrap-Example $ex) -Encoding UTF8
            $written++; $kept++
        }
    } catch {
        $detail = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { " :: " + $_.ErrorDetails.Message } else { "" }
        Log "  cycle $cycle ERROR: $($_.Exception.Message)$detail"
        Start-Sleep -Seconds 5
        continue
    }

    $elapsedMin = [int]((Get-Date) - $t0).TotalMinutes
    $rate = if ($elapsedMin -gt 0) { [math]::Round($written / $elapsedMin, 1) } else { 0 }
    Log ("  cycle {0,4}  {1,-22} {2,-12} +{3}/{4}  total={5}  ({6}/min)" -f $cycle, $lang, $task, $kept, $PerBatch, $written, $rate)

    $progress = [ordered]@{
        status = "generating"; updated = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
        cycle = $cycle; written = $written; model = $Model
        rate_per_min = $rate; deadline = $deadline.ToString("yyyy-MM-dd HH:mm")
    } | ConvertTo-Json -Compress
    Set-Content -LiteralPath $progressFile -Value $progress -Encoding UTF8
}

$why = if (Test-Path -LiteralPath $StopFile) { "stop file" } else { "deadline" }
Log "NIGHT OLLAMA GEN ends ($why)  $cycle cycles, $written total examples -> $Out"
$final = [ordered]@{
    status = "done"; updated = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    cycle = $cycle; written = $written; model = $Model; ended_reason = $why
} | ConvertTo-Json -Compress
Set-Content -LiteralPath $progressFile -Value $final -Encoding UTF8
if (Test-Path -LiteralPath $StopFile) { Remove-Item -LiteralPath $StopFile -Force }
