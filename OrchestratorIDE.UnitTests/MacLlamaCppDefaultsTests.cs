// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The llama.cpp lane on macOS: with no installer run, its runtime folder and model are found
/// the way the installer would set them, and llama-server is launched with arguments current
/// llama.cpp accepts. Settings the user filled in always win.
/// </summary>
[TestFixture]
public sealed class MacLlamaCppDefaultsTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "orc-maclcpp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Touch(string relative, DateTime? written = null)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        if (written is { } w)
            File.SetLastWriteTimeUtc(path, w);
        return path;
    }

    private string Sub(string name) => Path.Combine(_dir, name);

    // ── llama-server folder ──────────────────────────────────────────────────────────────

    [Test]
    public void A_configured_runtime_folder_always_wins()
    {
        Touch("installer/llama-b1/llama-server");
        Assert.That(MacLlamaCppDefaults.ResolveRuntimePath("/my/llama", Sub("installer"), []),
            Is.EqualTo("/my/llama"));
    }

    [Test]
    public void The_installers_newest_extracted_build_is_used_first()
    {
        Touch("installer/llama-b100/llama-server", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch("installer/llama-b200/llama-server", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch("brew/llama-server");

        Assert.That(MacLlamaCppDefaults.ResolveRuntimePath("", Sub("installer"), [Sub("brew")]),
            Is.EqualTo(Sub("installer/llama-b200")));
    }

    [Test]
    public void Homebrew_is_the_fallback_and_nothing_found_is_empty()
    {
        Touch("brew/llama-server");
        Assert.That(MacLlamaCppDefaults.ResolveRuntimePath("", Sub("no-installer"), [Sub("empty"), Sub("brew")]),
            Is.EqualTo(Sub("brew")));
        Assert.That(MacLlamaCppDefaults.ResolveRuntimePath("", Sub("no-installer"), [Sub("empty")]),
            Is.Empty);
    }

    // ── model ────────────────────────────────────────────────────────────────────────────

    private AppSettings Settings(string defaultModel, string lastSingle = "") => new()
    {
        ModelStoragePath = Sub("models"),
        DefaultModel = defaultModel,
        LastSingleModel = lastSingle,
    };

    [Test]
    public void A_configured_model_always_wins()
    {
        Touch("models/a.gguf");
        Assert.That(MacLlamaCppDefaults.ResolveModelPath("/my/model.gguf", Settings("a.gguf")),
            Is.EqualTo("/my/model.gguf"));
    }

    [Test]
    public void The_default_model_then_the_last_single_model_is_used()
    {
        var a = Touch("models/a.gguf");
        var b = Touch("models/b.gguf");
        Assert.That(MacLlamaCppDefaults.ResolveModelPath("", Settings("b.gguf", "a.gguf")), Is.EqualTo(b));
        Assert.That(MacLlamaCppDefaults.ResolveModelPath("", Settings("missing.gguf", "a.gguf")), Is.EqualTo(a));
    }

    [Test]
    public void Ollama_tags_are_skipped_for_the_first_gguf_in_the_model_folder()
    {
        var only = Touch("models/only.gguf");
        Assert.That(MacLlamaCppDefaults.ResolveModelPath("", Settings("qwen2.5-coder:14b")), Is.EqualTo(only));
    }

    [Test]
    public void Names_with_a_path_are_not_followed_and_no_model_is_empty()
    {
        Touch("outside.gguf");
        Assert.That(MacLlamaCppDefaults.ResolveModelPath("", Settings("../outside.gguf")), Is.Empty);
    }

    // ── launch arguments ─────────────────────────────────────────────────────────────────

    [Test]
    public void No_mmap_is_sent_except_on_macos()
    {
        var args = new LlamaServerManager { ModelPath = "/m.gguf" }.BuildArgs();
        // Current llama.cpp rejects --no-mmap and exits; Windows keeps its original flag.
        if (OperatingSystem.IsMacOS())
            Assert.That(args, Does.Not.Contain("--no-mmap"));
        else
            Assert.That(args, Does.Contain(" --no-mmap"));
        Assert.That(args, Does.Contain("--model \"/m.gguf\"").And.Contain("--metrics"));
    }
}
