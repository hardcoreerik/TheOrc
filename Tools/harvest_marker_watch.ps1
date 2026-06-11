# harvest_marker_watch.ps1 — stops NIGHT HARVEST at the ~1,000 train-data marker.
#
# Polls every 10 minutes: marker = approved train examples (manifest) plus
# staged captures awaiting review. At >= -Target it plants HARVEST_STOP so the
# harvest ends cleanly, and writes a morning note. Detached OS process — does
# not depend on any agent session staying alive.
param([int]$Target = 1000, [int]$PollMinutes = 10)

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$note = Join-Path $root "MARKER_NOTE.md"

while ($true) {
    $manifest = Get-Content "training_pit\datasets\manifests\reviewed_v1.json" -Raw | ConvertFrom-Json
    $entries  = $manifest.entries.PSObject.Properties.Value
    $approved = @($entries | Where-Object { $_.decision -eq 'approved' -and $_.split -eq 'train' }).Count
    $decided  = @($entries).Count
    $staged   = @(Get-ChildItem .orc\swarm\dataset-staging\plan_capture_good_*.json -ErrorAction SilentlyContinue).Count
    $awaiting = [Math]::Max(0, $staged - $decided)
    $total    = $approved + $awaiting

    "$(Get-Date -Format 'HH:mm') approved=$approved awaiting~=$awaiting total~=$total / $Target" |
        Add-Content (Join-Path $root ".orc\swarm\night_harvest\marker_watch.log")

    if ($total -ge $Target) {
        New-Item .orc\swarm\HARVEST_STOP -ItemType File -Force | Out-Null
        @"
# ~$Target train-data marker reached — $(Get-Date -Format 'yyyy-MM-dd HH:mm')

NIGHT HARVEST was stopped automatically: $approved approved + ~$awaiting staged
survivors >= $Target potential train examples for theorc-boss:gemma4.

Next steps (decisions, not automated):
1. Review the survivor queue (training_pit\batch_NH*_triage.tsv, high risk first).
2. "Next model" harvest: swarmcli farms whatever boss the app config points at —
   switching the captured model means changing that config AND starting a fresh
   dataset namespace (captures tag boss_model, so provenance stays clean).
"@ | Set-Content $note
        break
    }
    Start-Sleep -Seconds ($PollMinutes * 60)
}
