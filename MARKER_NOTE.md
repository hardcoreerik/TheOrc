# ~1000 train-data marker reached — 2026-06-11 11:51

NIGHT HARVEST was stopped automatically: 163 approved + ~846 staged
survivors >= 1000 potential train examples for theorc-boss:gemma4.

Next steps (decisions, not automated):
1. Review the survivor queue (training_pit\batch_NH*_triage.tsv, high risk first).
2. "Next model" harvest: swarmcli farms whatever boss the app config points at —
   switching the captured model means changing that config AND starting a fresh
   dataset namespace (captures tag boss_model, so provenance stays clean).
