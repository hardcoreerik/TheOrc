@echo off
REM Overnight Ollama gold generator launcher for HARDCOREPC (RTX 3050 6GB).
REM Invoked by the scheduled task "TheOrcNightGen". Runs until 06:00.
powershell -NoProfile -ExecutionPolicy Bypass -File F:\Ai\TheOrchestrator\tools\night_ollama_gen.ps1 -Seed F:\Ai\TheOrchestrator\training_pit\datasets\seed_boss_1384.jsonl -Out F:\Ai\TheOrchestrator\training_pit\datasets\ollama_gold_hcp.work.jsonl -Model qwen2.5-coder:7b -Until 06:00 -PerBatch 8
