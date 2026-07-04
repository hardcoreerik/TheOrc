// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text.Json;

namespace OrchestratorSetup.Services;

/// <summary>
/// Installs cudart64_12.dll, cublas64_12.dll, and cublasLt64_12.dll -- the CUDA runtime
/// redistributables OrchestratorIDE's in-process LLamaSharp backend
/// (OrchestratorIDE.NativeRuntime's NativeBackendBootstrap/LLamaSharpRuntime) needs to load its
/// cuda12 backend, but which LLamaSharp.Backend.Cuda12.Windows's NuGet package does not itself
/// ship (see that project's own csproj comment). Every fleet dev machine sourced these from a
/// locally installed CUDA Toolkit; a real end user has neither a toolkit nor a reason to install
/// one just for this, so the installer fetches them directly instead -- conditionally, only for
/// detected NVIDIA hardware, so AMD/Intel/CPU-only installs never pay for a download they can't
/// use.
///
/// Source: NVIDIA's own official CUDA redistributable manifest feed -- the same channel
/// conda/pip use to build their nvidia-cuda-runtime-cu12/nvidia-cublas-cu12 packages, not a
/// third-party NuGet repackaging and not a full multi-GB Toolkit installer. Mirrors
/// Tools/Get-CudaRedistributables.ps1 (used by the release CI build to fix the SAME gap for
/// the build machine's own published artifact) but reuses this project's own DownloadService
/// (resumable, SHA-256-verified, retry-on-failure -- already exercised by every other download
/// this installer performs) and ZipExtractService (zip-slip-guarded extraction) rather than
/// hand-rolling HTTP/hashing/extraction a second time.
/// </summary>
public sealed class CudaRedistributableInstaller
{
    private const string ManifestVersion = "12.4.0";
    private const string RedistBaseUrl   = "https://developer.download.nvidia.com/compute/cuda/redist";

    // Only the two NVIDIA redistributable components that contain the three DLLs we need.
    private static readonly string[] Components = ["cuda_cudart", "libcublas"];
    private static readonly string[] NeededDlls  = ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];

    private readonly DownloadService  _dl;
    private readonly ZipExtractService _zip;

    /// <summary>Log line for the scrolling install log -- same event shape InstallOrchestrator already relays.</summary>
    public event Action<string>? OnLog;

    public CudaRedistributableInstaller(DownloadService downloadService, ZipExtractService zipExtractService)
    {
        _dl  = downloadService;
        _zip = zipExtractService;
    }

    /// <summary>
    /// Ensures all three redistributable DLLs exist in <paramref name="targetDir"/> (the
    /// in-process runtime's expected "runtimes/win-x64/native/cuda12" directory, relative to
    /// the installed app). Idempotent: a re-run (repair install, upgrade) with all three DLLs
    /// already present skips the network entirely. Returns false (non-fatal to the overall
    /// install -- callers should log a warning and continue, matching how model download
    /// failures are already handled) if the manifest, download, or extraction fails.
    /// </summary>
    public async Task<bool> InstallAsync(string targetDir, CancellationToken ct)
    {
        if (NeededDlls.All(dll => File.Exists(Path.Combine(targetDir, dll))))
        {
            Log("CUDA runtime redistributables already present -- skipping.");
            return true;
        }

        Directory.CreateDirectory(targetDir);
        var workDir = Path.Combine(Path.GetTempPath(), $"theorc-cuda-redist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            JsonElement manifest;
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("OrchestratorSetup/1.0");
                var manifestUrl = $"{RedistBaseUrl}/redistrib_{ManifestVersion}.json";
                Log($"Fetching NVIDIA CUDA redistributable manifest ({ManifestVersion})...");
                var manifestJson = await http.GetStringAsync(manifestUrl, ct);
                manifest = JsonDocument.Parse(manifestJson).RootElement;
            }

            foreach (var component in Components)
            {
                if (!manifest.TryGetProperty(component, out var comp) ||
                    !comp.TryGetProperty("windows-x86_64", out var entry))
                {
                    Log($"NVIDIA manifest is missing a windows-x86_64 entry for '{component}' -- CUDA acceleration will not be available.");
                    return false;
                }

                var relativePath = entry.GetProperty("relative_path").GetString()
                    ?? throw new InvalidOperationException($"Manifest entry for '{component}' has no relative_path.");
                var sha256   = entry.GetProperty("sha256").GetString();
                var sizeStr  = entry.TryGetProperty("size", out var sizeProp) ? sizeProp.GetString() : null;
                var size     = long.TryParse(sizeStr, out var s) ? s : (long?)null;

                var downloadUrl = $"{RedistBaseUrl}/{relativePath}";
                var zipPath     = Path.Combine(workDir, Path.GetFileName(relativePath));

                Log($"Downloading {component}...");
                await _dl.DownloadFileAsync(downloadUrl, zipPath, component, size, sha256, ct);

                var extractDir = Path.Combine(workDir, Path.GetFileNameWithoutExtension(relativePath));
                await _zip.ExtractAsync(zipPath, extractDir, ct);

                var binDir = Directory.GetDirectories(extractDir, "bin", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (binDir is null)
                {
                    Log($"Could not find a 'bin' directory inside the extracted {component} archive -- CUDA acceleration will not be available.");
                    return false;
                }

                foreach (var dllPath in Directory.GetFiles(binDir, "*.dll"))
                {
                    var name = Path.GetFileName(dllPath);
                    if (!NeededDlls.Contains(name)) continue;
                    File.Copy(dllPath, Path.Combine(targetDir, name), overwrite: true);
                    Log($"  Installed {name}");
                }
            }

            var missing = NeededDlls.Where(dll => !File.Exists(Path.Combine(targetDir, dll))).ToList();
            if (missing.Count > 0)
            {
                Log($"Missing expected DLL(s) after extraction: {string.Join(", ", missing)} -- CUDA acceleration will not be available.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"CUDA redistributable install failed: {ex.Message} -- CUDA acceleration will not be available, but the rest of the install can continue.");
            return false;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
