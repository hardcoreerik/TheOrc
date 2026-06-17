Set-Location 'F:\Ai\OrchestratorIDE-dev'
Write-Host '=== Converting lora_v2 to GGUF ===' -ForegroundColor Cyan
python 'F:\Ai\llama.cpp\convert_lora_to_gguf.py' 
    'training_pit/outputs/lora_v2/adapter' 
    --base 'C:\Users\hardc\.cache\huggingface\hub\models--google--gemma-4-12b-it\snapshots\5926caa4ec0cac5cbfadaf4077420520de1d5205' 
    --outfile 'marketplace\theorc-boss-v2\theorc-boss-v2-lora.gguf' 
    --outtype f16
Write-Host "Exit: " -ForegroundColor Cyan
