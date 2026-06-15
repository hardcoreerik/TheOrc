// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text.Json;

namespace OrchestratorSetup.Services;

/// <summary>
/// Resolves the latest llama.cpp release asset URL from the GitHub Releases API
/// so installer links are never stale regardless of build number changes.
///
/// The llama.cpp project frequently changes its release naming convention
/// (e.g. "llama-b5200-bin-win-cuda-cu12.2.0-x64.zip" → "cudart-llama-bin-win-cuda-12.4-x64.zip").
/// Dynamic resolution avoids 404 errors caused by hardcoded build numbers.
/// </summary>
public static class LlamaCppResolver
{
    private const string ApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";

    // Terms that MUST appear in the asset filename (case-insensitive) for each variant.
    private static readonly Dictionary<string, string[]> MustContain = new()
    {
        ["cuda12"] = ["win", "cuda-12", "x64", ".zip"],
        ["cuda11"] = ["win", "cuda-11", "x64", ".zip"],
        ["vulkan"] = ["win", "vulkan",  "x64", ".zip"],
        ["avx2"]   = ["win", "avx2",    "x64", ".zip"],
        ["cpu"]    = ["win", "x64", ".zip"],
    };

    // Terms that must NOT appear in the asset filename for each variant
    // (prevents cpu/avx2 from matching cuda/vulkan builds).
    private static readonly Dictionary<string, string[]> MustNotContain = new()
    {
        ["vulkan"] = ["cuda"],
        ["avx2"]   = ["cuda", "vulkan"],
        ["cpu"]    = ["cuda", "vulkan", "avx2"],
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
            var root = JsonDocument.Parse(json).RootElement;

            if (!root.TryGetProperty("assets", out var assets))
                return null;

            var must    = MustContain.GetValueOrDefault(variant, []);
            var mustNot = MustNotContain.GetValueOrDefault(variant, []);

            // Prefer assets with "cudart-" prefix (self-contained — bundled CUDA runtime)
            // over those without it, for cuda variants.
            string? best    = null;
            string? fallback = null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProp)) continue;
                var name  = nameProp.GetString() ?? "";
                var lower = name.ToLowerInvariant();

                if (!must.All(f    => lower.Contains(f)))    continue;
                if ( mustNot.Any(f => lower.Contains(f)))    continue;

                var url = asset.TryGetProperty("browser_download_url", out var urlProp)
                              ? urlProp.GetString()
                              : null;
                if (url is null) continue;

                // Prefer the "cudart-" build (includes CUDA runtime DLLs — no extra install needed)
                if (lower.StartsWith("cudart-"))
                    best = url;
                else
                    fallback ??= url;
            }

            return best ?? fallback;
        }
        catch
        {
            return null; // non-fatal — InstallOrchestrator falls back to manifest URL
        }
    }
}
