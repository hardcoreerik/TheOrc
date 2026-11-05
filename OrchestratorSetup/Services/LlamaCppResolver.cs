// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OrchestratorSetup.Services;

/// <summary>
/// Resolves the latest llama.cpp release asset URL from the GitHub Releases API
/// so installer links are never stale regardless of build number changes.
///
/// The llama.cpp project frequently changes its release naming convention
/// (e.g. "llama-b5200-bin-win-cuda-cu12.2.0-x64.zip" → "cudart-llama-bin-win-cuda-12.4-x64.zip").
/// Dynamic resolution avoids 404 errors caused by hardcoded build numbers.
///
/// OS-aware as of INSTALLER_REVAMP_SPEC.md/MULTI_OS_RELEASE_SPEC.md Phase D -- terms used to
/// hardcode "win" into every variant, so this could never resolve a real asset for Linux or
/// macOS regardless of which RuntimeVariant the hardware detector picked. Verified against
/// the actual current release (`gh api repos/ggml-org/llama.cpp/releases/latest`, 2026-06-21)
/// rather than guessed: llama.cpp also quietly renamed Windows' CPU build from
/// "...-win-avx2-x64.zip" to "...-win-cpu-x64.zip" since this resolver was last touched --
/// our own "avx2" variant's match terms were already silently broken on Windows before any
/// of this Mac work, fixed here too while in this code.
///
/// macOS ships ONE unified build per architecture (no separate CUDA/Vulkan-equivalent split
/// -- Metal is always available), matching MacPlatformInstaller.DetectHardwareAsync's own
/// "metal" RuntimeVariant. Linux real coverage is currently vulkan/cpu only -- no plain CUDA
/// build exists in the current release (ROCm/SYCL exist but this codebase's variant taxonomy
/// has no slot for them yet); cuda11/cuda12 on Linux will fail closed (no match -> caller
/// falls back to the manifest's static URL, which also has no real Linux CUDA entry) until
/// that's deliberately scoped, not silently pretended to work.
///
/// Found 2026-06-24 testing on a real ARM64 Linux box (Raspberry Pi): the "cpu"/"avx2" must-
/// contain term ("cpu") never matched anything on Linux at all, x64 or arm64 -- Linux's
/// baseline build carries no backend label in its filename, unlike Windows' explicit
/// "win-cpu-*.zip". Fixed in TryResolveLatestAsync (OS-conditional must-contain terms).
/// **Not fixed**: Setup/model-manifest.json's static fallback table (used only if the GitHub
/// API call above fails) has ZERO Linux entries at all -- every variant value is a "win-"
/// filename, same gap the "app" manifest key already has for Linux deliberately (see
/// MULTI_OS_RELEASE_SPEC.md Phase B: "linux key intentionally absent... until [a Linux release
/// leg] lands"). Extending the static fallback to be OS-keyed (mirroring how the "app" key
/// already does windows/macos) is a real, separate, larger schema change -- flagged, not done
/// here alongside a same-day matching-logic bug fix.
/// </summary>
public static class LlamaCppResolver
{
    // NOT /releases/latest: llama.cpp publishes its numbered builds (bNNNNN, the releases that
    // carry the binaries) as PRERELEASES, which /latest never returns. Since 2026-11-04 /latest
    // is a new-style "v0.9.0" release whose only asset is nightly-tag.txt, so it resolved no
    // build on any OS. List recent releases instead (prereleases included, newest first) and
    // take the newest one with a matching asset -- see SelectAssetUrl.
    private const string ApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15";

    // Terms that MUST appear in the asset filename (case-insensitive) for each variant,
    // BEFORE the OS-specific tag/extension (added in TryResolveLatestAsync below) -- kept
    // OS-agnostic here so one table serves every platform instead of three near-duplicates.
    private static readonly Dictionary<string, string[]> MustContain = new()
    {
        ["cuda12"] = ["cuda-12"],
        ["cuda11"] = ["cuda-11"],
        ["vulkan"] = ["vulkan"],
        ["avx2"]   = ["cpu"], // llama.cpp dropped the "avx2" filename label; "cpu" is the
                              // unified CPU build now -- see class doc comment.
        ["cpu"]    = ["cpu"],
        ["metal"]  = [],      // macOS's OS tag alone ("macos") is sufficient to identify the
                              // one build that exists per architecture -- no extra term needed.
    };

    // Every accelerated build llama.cpp publishes alongside the plain CPU one (b12130,
    // 2026-11-05). The CPU variants must exclude them all: Linux's CPU asset has no "cpu" label
    // ("…-bin-ubuntu-x64.tar.gz"), so excluding only cuda/vulkan picked
    // "…-bin-ubuntu-openvino-2026.4.1-x64.tar.gz", the first remaining match.
    private static readonly string[] AcceleratedBuilds =
        ["cuda", "vulkan", "rocm", "sycl", "openvino", "opencl", "snapdragon", "zdnn"];

    // Terms that must NOT appear in the asset filename for each variant
    // (prevents cpu/avx2 from matching accelerated builds).
    private static readonly Dictionary<string, string[]> MustNotContain = new()
    {
        ["vulkan"] = ["cuda"],
        ["avx2"]   = AcceleratedBuilds,
        ["cpu"]    = AcceleratedBuilds,
    };

    /// <summary>
    /// (osTag, fileExtension) for the running OS -- e.g. ("win", ".zip") -- appended to
    /// <see cref="MustContain"/>'s per-variant terms so the same table works for every OS.
    /// "ubuntu" is llama.cpp's own naming for its generic Linux builds (not actually
    /// distro-specific despite the name).
    /// </summary>
    private static (string OsTag, string Extension) ResolveOsTagAndExtension() =>
        OperatingSystem.IsWindows() ? ("win", ".zip") :
        OperatingSystem.IsMacOS()   ? ("macos", ".tar.gz") :
        OperatingSystem.IsLinux()   ? ("ubuntu", ".tar.gz") :
        throw new PlatformNotSupportedException("LlamaCppResolver has no asset-naming mapping for this OS.");

    /// <summary>
    /// llama.cpp publishes both "x64" and "arm64" builds per OS (e.g. macos-arm64.tar.gz AND
    /// macos-x64.tar.gz both exist) -- without this, the resolver could grab a non-native
    /// binary for the machine it's actually installing onto (verified against the real release
    /// asset list, 2026-06-21, not assumed -- the previous Windows-only table got away without
    /// this because every single Windows asset happens to already contain the literal "x64"
    /// substring, hiding the gap until macOS/arm64 needed a real choice made).
    /// </summary>
    private static string ResolveArchTag() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        var other => throw new PlatformNotSupportedException(
            $"LlamaCppResolver has no asset-naming mapping for architecture '{other}'."),
    };

    /// <summary>
    /// Calls the GitHub Releases API and returns the <c>browser_download_url</c>
    /// of the best-matching asset for <paramref name="variant"/>.
    /// Returns <c>null</c> if the API is unreachable or no matching asset is found
    /// — the caller should then fall back to the manifest's static URL.
    /// </summary>
    public static async Task<string?> TryResolveLatestAsync(string variant,
                                                             CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Add("User-Agent", "TheOrc-Installer/1.0");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var json = await http.GetStringAsync(ApiUrl, ct);
            var (osTag, extension) = ResolveOsTagAndExtension();
            return SelectAssetUrl(JsonDocument.Parse(json).RootElement, variant, osTag,
                                  ResolveArchTag(), extension);
        }
        catch
        {
            return null; // non-fatal — InstallOrchestrator falls back to manifest URL
        }
    }

    /// <summary>
    /// The download URL of the first matching asset in the newest release that has one.
    /// <paramref name="releases"/> is the GitHub API's release list (newest first); a single
    /// release object is accepted too. Drafts are skipped; prereleases are not (that is where
    /// llama.cpp's builds are). Pure: no network, so it's testable against real API payloads.
    /// </summary>
    internal static string? SelectAssetUrl(JsonElement releases, string variant,
                                           string osTag, string archTag, string extension)
    {
        IEnumerable<JsonElement> list = releases.ValueKind == JsonValueKind.Array
            ? releases.EnumerateArray()
            : [releases];

        // llama.cpp's Linux ("ubuntu") CPU build carries no backend label at all -- unlike
        // Windows' explicit "win-cpu-x64.zip"/"win-cpu-arm64.zip", the Linux asset is just
        // "ubuntu-x64.tar.gz"/"ubuntu-arm64.tar.gz" with zero backend substring (verified
        // live against the real release, 2026-06-24: gh api .../releases/latest lists both
        // "llama-bNNNN-bin-ubuntu-x64.tar.gz" and "...-ubuntu-arm64.tar.gz", neither
        // containing "cpu" anywhere). Requiring the literal "cpu" term there meant this
        // variant has never matched anything on Linux, on any architecture -- same class of
        // bug as the avx2->cpu rename noted in this class's doc comment, just OS-specific
        // instead of time-specific. MustNotContain's cuda/vulkan exclusion already does the
        // real discriminating work once "cpu" stops being a hard requirement, exactly like
        // "metal" already needs no extra must-contain term on macOS.
        var cpuLikeVariant = variant is "cpu" or "avx2";
        var variantTerms = (osTag == "ubuntu" && cpuLikeVariant)
            ? []
            : MustContain.GetValueOrDefault(variant, []);
        var must = variantTerms.Append(osTag).Append(archTag).Append(extension).ToArray();
        var mustNot = MustNotContain.GetValueOrDefault(variant, []);

        foreach (var release in list)
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProp)) continue;
                var lower = (nameProp.GetString() ?? "").ToLowerInvariant();

                // "cudart-llama-bin-win-cuda-*.zip" holds ONLY the CUDA runtime DLLs
                // (cublas64_12.dll, cublasLt64_12.dll, cudart64_12.dll -- verified against
                // b12130's zip, 2026-11-05): no llama-server.exe. It used to be preferred as
                // "self-contained", which handed the installer an archive with no server in it.
                // The binaries are in "llama-bNNNNN-bin-win-cuda-*.zip".
                if (lower.StartsWith("cudart-")) continue;

                if (!must.All(f    => lower.Contains(f)))    continue;
                if ( mustNot.Any(f => lower.Contains(f)))    continue;

                if (asset.TryGetProperty("browser_download_url", out var urlProp) &&
                    urlProp.GetString() is { Length: > 0 } url)
                    return url;
            }
        }
        return null;
    }
}
