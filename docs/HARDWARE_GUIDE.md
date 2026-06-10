# TheOrc — Hardware Guide

> **Quick summary:** TheOrc runs on any NVIDIA, AMD, or Intel GPU. CPU-only is supported
> but limited to small models. The reference dev machine is RTX 5070 Ti (16 GB).

---

## Inference Hardware

### NVIDIA (CUDA)

| GPU | VRAM | CUDA | Inference backend |
|---|---|---|---|
| RTX 50xx (Blackwell) | 12–24 GB | 12+ | llama.cpp cuda12 |
| RTX 40xx (Ada) | 8–24 GB | 12+ | llama.cpp cuda12 |
| RTX 30xx (Ampere) | 8–24 GB | 11–12 | llama.cpp cuda11 or cuda12 |
| GTX 16xx / 10xx | 4–8 GB | 11 | llama.cpp cuda11 |

CUDA version check:
```powershell
nvidia-smi | Select-String "CUDA Version"
```

### AMD (Vulkan)

| GPU | VRAM | Backend |
|---|---|---|
| RX 7000 series | 8–16 GB | llama.cpp Vulkan |
| RX 6000 series | 8–16 GB | llama.cpp Vulkan |

AMD performance is good for inference. Training is not supported via llama.cpp Vulkan —
use WSL2 with ROCm for training (not documented here).

### Intel Arc (Vulkan)

| GPU | VRAM | Backend |
|---|---|---|
| Arc A770 / A750 | 8–16 GB | llama.cpp Vulkan |
| Arc A380 | 6 GB | llama.cpp Vulkan |

### CPU-Only (AVX2 / baseline)

CPU inference is supported but limited:

| CPU capability | Backend | Practical models |
|---|---|---|
| AVX2 | llama.cpp avx2 | 3B Q8, 7B Q4 |
| Baseline | llama.cpp cpu | 1.5B–3B only |

CPU inference is very slow for anything over 3B. Suitable for evaluation, not production use.

---

## VRAM Quick Reference for Inference

| VRAM | What fits | Notes |
|---|---|---|
| 4 GB | 3B Q8 / 7B Q4 | Very limited; mostly CPU-assisted inference |
| 6 GB | 7B Q5 | Smallest tier with reliable coding capability |
| 8 GB | 7B Q8 / 12B Q4 | Good for Qwen 7B or Gemma 4 12B Q4 |
| 10–12 GB | 14B Q4 | Qwen 14B fits; strong coder/researcher |
| 16 GB | 14B Q5 / 22B Q4 | `theorc-boss:gemma4` + Qwen 14B worker; very capable |
| 24 GB+ | 32B Q4 | Qwen 2.5 Coder 32B — best available locally |

---

## TheOrc Dev Machine Reference

| Component | Spec |
|---|---|
| GPU | NVIDIA GeForce RTX 5070 Ti |
| VRAM | 16 GB |
| System RAM | ≥32 GB (recommended for swarm + training) |
| OS | Windows 11 64-bit |
| Ollama | 0.30.6+ |
| Backend | Ollama via `/v1/chat/completions` |
| Training tier | QLoRA NF4 on 12–14B models (fits with headroom) |

---

## Swarm Hardware Considerations

Running a full swarm (Boss + 3–4 workers) requires Ollama to load multiple models
concurrently. Options:

### Option A — Single GPU (most common)

Load one model at a time and rely on `OLLAMA_NUM_PARALLEL` to queue.
Workers run sequentially unless the GPU can fit multiple models.

```powershell
$env:OLLAMA_NUM_PARALLEL = "4"
ollama serve
```

With `OLLAMA_NUM_PARALLEL`, Ollama can handle concurrent requests but will context-switch
between models, reducing per-model speed. VRAM budget is shared.

### Option B — Two GPUs

Dedicated GPU per model (Boss on GPU 0, workers on GPU 1). Ollama handles multi-GPU
automatically if both GPUs are visible.

### Option C — Remote Ollama

Point TheOrc at a remote Ollama server with more VRAM or more GPUs:
**Settings → Ollama Host** → set to `http://<remote-ip>:11434`

---

## Training Hardware

Training is only relevant when Phase 3 of the Training Pit is unblocked.
See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for current phase status.

| VRAM | Training mode | Use case |
|---|---|---|
| 8 GB | QLoRA 4-bit only | Small models (≤7B); marginal for 12B |
| 12 GB | QLoRA 4-bit | 12B QLoRA — fits but tight |
| 16 GB | QLoRA NF4 | 12B QLoRA — fits with ~2 GB headroom (recommended) |
| 24 GB | LoRA BF16 (7B), QLoRA (32B) | More comfortable; 12B LoRA still requires ~26.7 GB |
| 40 GB+ | LoRA BF16 (12B+) | Full-precision training |

**Note:** Gemma 4 12B BF16 (for LoRA training) is **26.7 GB** — confirmed by Google AI docs.
It does not fit at 16 GB for LoRA. Use QLoRA NF4 at 16 GB.

### Training: Windows vs WSL2

**Training should be done in WSL2, not Windows native.**

- PyTorch + CUDA training is better supported on Linux
- Unsloth CUDA kernels are Linux-optimized
- Package management (pip, conda) is simpler on Linux

```bash
# WSL2 setup for training
pip install unsloth[cu124]   # Match your actual CUDA version
python -c "import torch; print(torch.cuda.get_device_name(0))"
```

TheOrc's WPF app runs on Windows. Training tools run in WSL2. These are separate.

---

## System RAM

| Use case | Minimum | Recommended |
|---|---|---|
| Inference only | 16 GB | 32 GB |
| Swarm with 3–4 workers | 16 GB | 32 GB |
| QLoRA training (12B) | 32 GB | 64 GB |

The paged optimizer (`paged_adamw_8bit`) offloads optimizer states to CPU RAM when VRAM
is full. With 32 GB system RAM, this is effectively unlimited for 12B model training.

---

## Training Time Estimates (16 GB VRAM)

For gemma4:12b QLoRA on RTX 5070 Ti:

| Dataset size | Epochs | Est. time |
|---|---|---|
| 100 examples | 3 | ~0.5 hours |
| 300 examples | 3 | ~1.5 hours |
| 500 examples | 3 | ~2.5 hours |

Assumes: Unsloth + CUDA 12.4, batch size 1 + gradient accumulation 8, sequence length 2048.

---

## Hardware Detection

TheOrc auto-detects GPU hardware at startup and startup install using WMI:

```powershell
# PowerShell: check what TheOrc sees
Get-WmiObject -Class Win32_VideoController | Select-Object Name, AdapterRAM
```

The `check_hardware.py` script gives a training-specific summary:
```bash
python training_pit/scripts/check_hardware.py
```

---

## Disk Space

| Item | Size |
|---|---|
| Qwen 2.5 Coder 7B Q5 | ~5 GB |
| Qwen 2.5 Coder 14B Q4 | ~8.5 GB |
| `theorc-boss:gemma4` (Q4_0 GGUF) | 6.7 GB |
| `google/gemma-4-12b-it` safetensors (for training) | ~26.7 GB |
| LoRA adapter after training | ~200 MB |
| GGUF export of fine-tuned model | ~8 GB |

Allow at least 50 GB free for a training pass (model weights + adapter + GGUF export + checkpoints).
