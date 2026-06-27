// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace OrchestratorIDE.Core;

// ── Inference backend selection ──────────────────────────────────────────────

/// <summary>
/// Which inference runtime The Orc connects to.
/// Ollama   — an external Ollama service (existing behaviour).
/// LlamaCpp — llama-server.exe managed directly by The Orc (installer path).
/// </summary>
public enum InferenceBackend { Ollama, LlamaCpp }

/// <summary>
/// User-configurable settings. Persisted to %APPDATA%\OrchestratorIDE\settings.json.
/// </summary>
public class AppSettings
{
    // ── Inference backend ─────────────────────────────────────────────────
    /// <summary>Which runtime to use. Default: Ollama (backwards-compatible).</summary>
    public InferenceBackend Backend { get; set; } = InferenceBackend.Ollama;

    // ── Ollama (Backend == Ollama) ────────────────────────────────────────
    public string OllamaHost    { get; set; } = "http://localhost:11434";

    // ── llama.cpp (Backend == LlamaCpp) ──────────────────────────────────
    /// <summary>Folder that contains llama-server.exe (and its CUDA/Vulkan DLLs).</summary>
    public string LlamaCppRuntimePath { get; set; } = "";

    /// <summary>Absolute path to the .gguf model file to load.</summary>
    public string LlamaCppModelPath   { get; set; } = "";

    /// <summary>Port the llama-server listens on. Default: 8080.</summary>
    public int    LlamaCppPort        { get; set; } = 8080;

    /// <summary>
    /// GPU layers to offload.
    ///  -1  = offload ALL layers (default — uses full GPU)
    ///   0  = CPU only
    ///  N>0 = offload exactly N layers
    /// </summary>
    public int    LlamaCppGpuLayers   { get; set; } = -1;

    /// <summary>Context window size in tokens. Default: 8192.</summary>
    public int    LlamaCppContextSize { get; set; } = 8192;

    /// <summary>CPU threads for token generation. 0 = auto-detect.</summary>
    public int    LlamaCppThreads     { get; set; } = 0;

    // ── Derived helper ────────────────────────────────────────────────────
    /// <summary>Base URL that OllamaClient should point at for the active backend.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string InferenceBaseUrl => Backend == InferenceBackend.LlamaCpp
        ? $"http://127.0.0.1:{LlamaCppPort}"
        : OllamaHost;

    // ── Model storage ────────────────────────────────────────────────────
    /// <summary>
    /// Directory where GGUF model files are downloaded.
    /// Empty = default: %APPDATA%\OrchestratorIDE\Models
    /// </summary>
    public string ModelStoragePath { get; set; } = "";

    /// <summary>
    /// Resolved model storage directory — always non-empty, never throws.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedModelStoragePath =>
        !string.IsNullOrEmpty(ModelStoragePath)
            ? ModelStoragePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", "Models");

    // ── Agent behaviour ──────────────────────────────────────────────────
    public string DefaultModel  { get; set; } = "qwen2.5-coder:14b";

    /// <summary>
    /// Last model used in Single-agent mode. Restored when switching to Single.
    /// Kept separate from swarm so switching modes doesn't clobber each other's pick.
    /// </summary>
    public string LastSingleModel { get; set; } = "";

    /// <summary>
    /// Last orchestrator (boss) model used in Swarm mode.
    /// This is the planning + merging model — typically a larger, smarter model.
    /// </summary>
    public string LastSwarmModel  { get; set; } = "";

    /// <summary>
    /// Last coder/uidev worker model used in Swarm mode.
    /// Defaults to the boss model when empty.
    /// Typically a smaller, faster model (e.g. nemotron-3-nano:4b-q8_0).
    /// </summary>
    public string LastWorkerModel { get; set; } = "";

    /// <summary>
    /// Researcher role model. Can be a lower-quant variant of the worker model to save
    /// VRAM during the research phase. Ollama evicts it before the coder phase loads.
    /// Empty = use the same model as LastWorkerModel.
    /// </summary>
    public string LastResearcherModel { get; set; } = "";
    public int    MaxStepsOverride { get; set; } = 0;   // 0 = use model profile default
    public bool   AutoVerify    { get; set; } = true;
    public bool   AutoCheckpoint { get; set; } = true;  // git commit before every Execute run

    /// <summary>
    /// When true, restores the model you had last time on startup (per-mode: single or swarm).
    /// When false, always auto-selects the best available model from the preferred list.
    /// </summary>
    public bool   RestoreLastModel { get; set; } = true;

    /// <summary>
    /// When true, auto-selects the best model from the preferred list on first run (no saved model),
    /// and switches to a security-focused model when a pentest workspace is detected.
    /// </summary>
    public bool   AutoModelSwitch { get; set; } = true;

    /// <summary>
    /// Activity log verbosity level.
    ///   1 = Silent, 2 = Default, 3 = Medium, 4 = High, 5 = Everything
    /// </summary>
    public int    ActivityVerbosity { get; set; } = 2;

    // ── Multi-agent / parallel inference ────────────────────────────────
    /// <summary>
    /// Desired OLLAMA_NUM_PARALLEL value. 1 = default (single slot).
    /// TheOrc stores this so the Settings panel can show the current preference
    /// and offer to apply it permanently to the Windows user environment.
    /// </summary>
    public int OllamaParallelSlots { get; set; } = 1;

    /// <summary>
    /// Last active agent mode — "single" (default) or "swarm".
    /// Restored on next launch so the app opens in the same mode the user left it.
    /// Swarm mode is silently demoted to single if the gate is not satisfied at startup.
    /// </summary>
    public string LastMode { get; set; } = "single";

    /// <summary>
    /// Active trust level for the approval gate.
    /// Persisted so the user's preferred level survives restarts.
    /// Default: Guarded (every write/shell needs explicit approval).
    /// </summary>
    public Trust.TrustLevel TrustLevel { get; set; } = Trust.TrustLevel.Guarded;

    // ── Workspace ────────────────────────────────────────────────────────
    public string DefaultWorkspace { get; set; } = "";

    /// <summary>
    /// Most-recently-used workspace folders, newest first. Max 10 entries.
    /// Used to auto-open the last workspace on startup and power the Open Recent menu.
    /// </summary>
    public List<string> RecentWorkspaces { get; set; } = [];

    // ── UI ────────────────────────────────────────────────────────────────
    public bool   ShowActivityLog  { get; set; } = true;
    public double ActivityLogHeight { get; set; } = 180;

    // ── Detected hardware (written by installer, read by app) ────────────
    /// <summary>GPU display name, e.g. "NVIDIA GeForce RTX 2070 SUPER".</summary>
    public string DetectedGpuName     { get; set; } = "";

    /// <summary>VRAM in GB as detected at install time.</summary>
    public double DetectedVramGb      { get; set; } = 0;

    /// <summary>Runtime variant chosen by the installer: cuda12, cuda11, vulkan, avx2, cpu.</summary>
    public string DetectedRuntime     { get; set; } = "";

    /// <summary>CUDA version string, e.g. "12.4". Empty if not NVIDIA.</summary>
    public string DetectedCudaVersion { get; set; } = "";

    // ── Self-improve ─────────────────────────────────────────────────────
    /// <summary>
    /// Local folder where the TheOrc GitHub source is cloned/pulled.
    /// Defaults to %AppData%\OrchestratorIDE\source.
    /// </summary>
    public string SourceFolderPath { get; set; } = "";

    // ── Personalisation (set by first-run wizard, editable in Settings) ──
    /// <summary>User's preferred name or handle shown to the agent.</summary>
    public string AgentUserName       { get; set; } = "";

    /// <summary>Any extra context the user wants the agent to always know.</summary>
    public string AgentExtraContext   { get; set; } = "";

    /// <summary>Set to true once the first-run personalisation wizard has completed.</summary>
    public bool   FirstRunComplete    { get; set; } = false;

    // ── HIVE MIND ─────────────────────────────────────────────────────────
    /// <summary>
    /// When true, this machine broadcasts its presence on the LAN (UDP port 7077)
    /// and serves a capability endpoint (HTTP port 7078) so other HIVE MIND nodes
    /// can discover it and route tasks to it.
    /// Set to true by the installer when the user checks "Join HIVE MIND".
    /// </summary>
    public bool HiveMindEnabled { get; set; } = false;

    /// <summary>
    /// Disables the constellation view's flowing-data/breathing-center animations,
    /// falling back to the static rendering. Off (animations on) by default; opt-in
    /// for machines where the animation loop isn't worth the CPU cost.
    /// </summary>
    public bool HiveLiteMode { get; set; } = false;

    /// <summary>HIVE screen layout: rail on the left side instead of the right. User-customizable
    /// (flip button on the rail); persisted so the choice sticks across sessions.</summary>
    public bool   HiveRailOnLeft { get; set; } = false;
    /// <summary>HIVE screen layout: width (px) of the metrics rail, set by dragging the splitter.
    /// Clamped to a sane range on load.</summary>
    public double HiveRailWidth  { get; set; } = 246;

    /// <summary>
    /// HIVE_MEMBERSHIP_SPEC.md §6.4 — default HiveAcceptControlPolicy applied to a newly
    /// paired peer at pairing-approval time, instead of the previously hardcoded "Ask".
    /// Stored as a string (not the enum directly) so a future enum rename/reorder can't
    /// silently corrupt an existing settings.json the way a numeric enum value would.
    /// Existing peers are unaffected -- this only changes the default for peers paired AFTER
    /// the setting is changed; change it per-peer via the HIVE panel's context menu instead.
    /// "Ask" remains the safe out-of-the-box default; the user opts into automation.
    /// </summary>
    public string HiveDefaultAcceptControlFrom { get; set; } = "Ask";

    /// <summary>
    /// Phase 3 — Distributed Swarm. When true AND HiveMindEnabled, this machine
    /// acts as a Warchief: it opens a HiveTaskQueue (port 7079) and distributes
    /// SwarmTasks to worker nodes instead of running them all locally.
    /// </summary>
    public bool HiveDistributedSwarm { get; set; } = false;

    /// <summary>
    /// Phase 3 — Worker Mode. When true, this machine runs a HiveWorkerAgent
    /// that polls the Warchief's task queue and executes tasks using its local Ollama.
    /// A machine can be both Warchief and Worker (default) or Worker-only.
    /// </summary>
    public bool HiveWorkerMode { get; set; } = false;

    /// <summary>
    /// Warchief URL this worker polls (e.g. "http://192.168.1.10:7079").
    /// Only used when HiveWorkerMode is true. Must be set to enable worker polling;
    /// leaving it empty disables the worker agent and logs a warning at startup.
    /// </summary>
    public string HiveWarchiefUrl { get; set; } = "";

    /// <summary>Optional trusted NodeId pin for the Warchief. Avoids stale DNS/address aliases selecting an old identity.</summary>
    public string HiveWarchiefNodeId { get; set; } = "";

    /// <summary>
    /// Comma-separated task roles this worker accepts (e.g. "researcher,coder").
    /// Empty = accept all roles. Used by HiveWorkerAgent lane filter.
    /// </summary>
    public string HiveWorkerLanes { get; set; } = "";

    /// <summary>
    /// Experimental Native Runtime opt-in for HIVE worker execution. Default remains false:
    /// normal chat, swarm, and worker paths keep using the configured IModelRuntime unless
    /// this is explicitly enabled.
    /// </summary>
    public bool ExperimentalNativeHiveWorkerEnabled { get; set; } = false;

    /// <summary>
    /// Experimental Native Runtime opt-in for the main single-agent chat loop. Default remains
    /// false: AgentLoop keeps using Ollama unless this is explicitly enabled. Independent of
    /// <see cref="ExperimentalNativeHiveWorkerEnabled"/> — enabling both at once builds two
    /// separate NativeRoleRuntime instances (no shared session yet), each loading its own copy
    /// of the base model into VRAM.
    /// </summary>
    public bool ExperimentalNativeMainChatEnabled { get; set; } = false;

    /// <summary>
    /// Root folder scanned by ModelDepot for native HIVE worker models/adapters.
    /// Empty = use ResolvedModelStoragePath.
    /// </summary>
    public string NativeRuntimeModelRoot { get; set; } = "";

    /// <summary>Native HIVE worker context window size. Default: 8192 tokens.</summary>
    public int NativeRuntimeContextSize { get; set; } = 8192;

    /// <summary>Native HIVE worker GPU layers. -1 = offload all supported layers.</summary>
    public int NativeRuntimeGpuLayers { get; set; } = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedNativeRuntimeModelRoot =>
        !string.IsNullOrWhiteSpace(NativeRuntimeModelRoot)
            ? NativeRuntimeModelRoot
            : ResolvedModelStoragePath;

    /// <summary>Port for the Warchief's HiveTaskQueue service. Default: 7079.</summary>
    public int HiveTaskQueuePort { get; set; } = 7079;

    /// <summary>
    /// Phase 1 — Worktree-per-task isolation (opt-in, default off).
    /// When true, SwarmSession routes each task's output through its own isolated
    /// git worktree (repo mode) or plain directory (greenfield mode), and merges
    /// back into a per-run integration tree. Enforced by WorktreeManager +
    /// FileOwnershipLedger. When false the existing flat-staging path is used.
    /// See docs/WORKTREE_ISOLATION_DESIGN.md for the full design.
    /// </summary>
    public bool HiveWorktreeIsolation { get; set; } = false;

    /// <summary>
    /// When true, SwarmSession runs the Reviewer Quality Gate (via Codex CLI) on
    /// staged output before the user applies it to the workspace. BLOCKER findings
    /// are surfaced in the Launch Pad; the user still decides whether to apply.
    /// Requires Codex CLI (npm i -g @openai/codex). Silently skipped if not installed.
    /// </summary>
    public bool SwarmReviewGateEnabled { get; set; } = true;

    /// <summary>Local Ollama reviewer mode. "Off" disables it; "Advisory" shows findings without blocking;
    /// "Gated" holds the run when BLOCKER findings are present.</summary>
    public string SwarmLocalReviewMode  { get; set; } = "Off";

    /// <summary>Ollama model used for local advisory/gated review. Must be installed in Ollama.</summary>
    public string SwarmLocalReviewModel { get; set; } = "qwen2.5-coder:14b";

    /// <summary>
    /// Local Ollama model for the Pit Boss training wizard.
    /// Default: Hermes-3 Llama 3.2 3B (2.0 GB) — safe to run alongside QLoRA training.
    /// Fallback to hermes3 (8B, 4.7 GB) for higher quality in sequential-only use.
    /// </summary>
    public string PitBossModel { get; set; } = "hf.co/NousResearch/Hermes-3-Llama-3.2-3B-GGUF:Q4_K_M";

    // ── Updates ───────────────────────────────────────────────────────────
    /// <summary>Whether to silently check GitHub for newer releases on startup.</summary>
    public bool      CheckForUpdates        { get; set; } = true;

    /// <summary>UTC timestamp of the last successful update check (null = never checked).</summary>
    public DateTime? LastUpdateCheckUtc     { get; set; } = null;

    /// <summary>Tag version string from the last check, e.g. "1.1.0". Empty = unknown.</summary>
    public string    LastKnownLatestVersion { get; set; } = "";

    // ── Persistence ───────────────────────────────────────────────────────
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "settings.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(text, _json) ?? new AppSettings();
            }
        }
        catch { /* corrupt settings — use defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, _json));
        }
        catch { /* non-fatal */ }
    }
}
