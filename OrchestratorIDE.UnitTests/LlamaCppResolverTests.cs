// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using NUnit.Framework;
using OrchestratorSetup.Services;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The installer's llama.cpp runtime lookup, against the real shape of ggml-org/llama.cpp's
/// releases (asset names copied from b12130, 2026-11-05). Since 2026-11-04 /releases/latest is
/// "v0.9.0" holding only nightly-tag.txt; the numbered builds are prereleases.
/// </summary>
[TestFixture]
public sealed class LlamaCppResolverTests
{
    private static readonly string[] B12130Assets =
    [
        "cudart-llama-b12130-bin-ubuntu-cuda-12.8-x64.tar.gz",
        "cudart-llama-b12130-bin-ubuntu-cuda-13.4-arm64.tar.gz",
        "cudart-llama-b12130-bin-ubuntu-cuda-13.4-x64.tar.gz",
        "cudart-llama-bin-win-cuda-12.4-x64.zip",
        "cudart-llama-bin-win-cuda-13.4-arm64.zip",
        "cudart-llama-bin-win-cuda-13.4-x64.zip",
        "llama-b12130-bin-android-arm64-snapdragon.tar.gz",
        "llama-b12130-bin-android-arm64.tar.gz",
        "llama-b12130-bin-linux-arm64-snapdragon.tar.gz",
        "llama-b12130-bin-macos-arm64.tar.gz",
        "llama-b12130-bin-macos-x64.tar.gz",
        "llama-b12130-bin-ubuntu-26.04-s390x.tar.gz",
        "llama-b12130-bin-ubuntu-arm64.tar.gz",
        "llama-b12130-bin-ubuntu-cuda-12.8-x64.tar.gz",
        "llama-b12130-bin-ubuntu-cuda-13.4-arm64.tar.gz",
        "llama-b12130-bin-ubuntu-cuda-13.4-x64.tar.gz",
        "llama-b12130-bin-ubuntu-openvino-2026.4.1-x64.tar.gz",
        "llama-b12130-bin-ubuntu-rocm-10.0-x64.tar.gz",
        "llama-b12130-bin-ubuntu-s390x.tar.gz",
        "llama-b12130-bin-ubuntu-sycl-fp16-x64.tar.gz",
        "llama-b12130-bin-ubuntu-sycl-fp32-x64.tar.gz",
        "llama-b12130-bin-ubuntu-vulkan-arm64.tar.gz",
        "llama-b12130-bin-ubuntu-vulkan-x64.tar.gz",
        "llama-b12130-bin-ubuntu-x64.tar.gz",
        "llama-b12130-bin-ubuntu-zdnn-s390x.tar.gz",
        "llama-b12130-bin-win-cpu-arm64.zip",
        "llama-b12130-bin-win-cpu-x64.zip",
        "llama-b12130-bin-win-cuda-12.4-x64.zip",
        "llama-b12130-bin-win-cuda-13.4-arm64.zip",
        "llama-b12130-bin-win-cuda-13.4-x64.zip",
        "llama-b12130-bin-win-opencl-adreno-arm64.zip",
        "llama-b12130-bin-win-openvino-2026.4.1-x64.zip",
        "llama-b12130-bin-win-rocm-10.0-x64.zip",
        "llama-b12130-bin-win-sycl-x64.zip",
        "llama-b12130-bin-win-vulkan-arm64.zip",
        "llama-b12130-bin-win-vulkan-x64.zip",
        "llama-b12130-ui.tar.gz",
        "llama-b12130-xcframework.zip",
    ];

    private static object Release(string tag, IEnumerable<string> assets, bool prerelease = false, bool draft = false) => new
    {
        tag_name = tag,
        prerelease,
        draft,
        assets = assets.Select(n => new { name = n, browser_download_url = $"https://dl.example/{tag}/{n}" }),
    };

    /// <summary>The list endpoint as it looks now: "latest" v0.9.0 first, builds as prereleases.</summary>
    private static JsonElement CurrentReleaseList() => JsonSerializer.SerializeToElement(new[]
    {
        Release("v0.9.0", ["nightly-tag.txt"]),
        Release("b12130", B12130Assets, prerelease: true),
        Release("b12129", B12130Assets.Select(a => a.Replace("b12130", "b12129")), prerelease: true),
    });

    private static string? Pick(string variant, string os, string arch, string ext) =>
        LlamaCppResolver.SelectAssetUrl(CurrentReleaseList(), variant, os, arch, ext) is { } url
            ? url[(url.LastIndexOf('/') + 1)..]
            : null;

    [TestCase("cuda12", "win",    "x64",   ".zip",    "llama-b12130-bin-win-cuda-12.4-x64.zip")]
    [TestCase("vulkan", "win",    "x64",   ".zip",    "llama-b12130-bin-win-vulkan-x64.zip")]
    [TestCase("cpu",    "win",    "x64",   ".zip",    "llama-b12130-bin-win-cpu-x64.zip")]
    [TestCase("avx2",   "win",    "x64",   ".zip",    "llama-b12130-bin-win-cpu-x64.zip")]
    [TestCase("cpu",    "win",    "arm64", ".zip",    "llama-b12130-bin-win-cpu-arm64.zip")]
    [TestCase("metal",  "macos",  "arm64", ".tar.gz", "llama-b12130-bin-macos-arm64.tar.gz")]
    [TestCase("cuda12", "ubuntu", "x64",   ".tar.gz", "llama-b12130-bin-ubuntu-cuda-12.8-x64.tar.gz")]
    [TestCase("vulkan", "ubuntu", "x64",   ".tar.gz", "llama-b12130-bin-ubuntu-vulkan-x64.tar.gz")]
    [TestCase("cpu",    "ubuntu", "x64",   ".tar.gz", "llama-b12130-bin-ubuntu-x64.tar.gz")]
    [TestCase("cpu",    "ubuntu", "arm64", ".tar.gz", "llama-b12130-bin-ubuntu-arm64.tar.gz")]
    public void Picks_the_newest_build_for_each_variant(string variant, string os, string arch, string ext, string expected) =>
        Assert.That(Pick(variant, os, arch, ext), Is.EqualTo(expected));

    [Test]
    public void Never_picks_a_cudart_zip_which_holds_only_the_CUDA_runtime_DLLs() =>
        Assert.That(Pick("cuda12", "win", "x64", ".zip"), Does.Not.StartWith("cudart-"));

    [Test]
    public void The_old_latest_release_alone_has_nothing_to_install()
    {
        // What /releases/latest returns today: why every install came back empty.
        var latest = JsonSerializer.SerializeToElement(Release("v0.9.0", ["nightly-tag.txt"]));
        Assert.That(LlamaCppResolver.SelectAssetUrl(latest, "cuda12", "win", "x64", ".zip"), Is.Null);
    }

    [Test]
    public void Drafts_are_skipped_and_no_match_is_null()
    {
        var releases = JsonSerializer.SerializeToElement(new[]
        {
            Release("b99999", B12130Assets, prerelease: true, draft: true),
            Release("b12130", B12130Assets, prerelease: true),
        });
        Assert.That(LlamaCppResolver.SelectAssetUrl(releases, "cpu", "win", "x64", ".zip"), Does.Contain("/b12130/"));
        Assert.That(LlamaCppResolver.SelectAssetUrl(releases, "cuda11", "win", "x64", ".zip"), Is.Null,
            "no CUDA 11 build is published any more: the caller falls back to the manifest URL");
    }
}
