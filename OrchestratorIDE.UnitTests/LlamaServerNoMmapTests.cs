// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using NoMmap = OrchestratorIDE.Core.LlamaServerManager.NoMmapFlag;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// "Don't memory-map the model" for llama-server: current builds reject --no-mmap and exit
/// (it became --load-mode), older builds only know --no-mmap. The flag is picked from the
/// binary's own --help. macOS sends neither (mmap is right with unified memory).
/// </summary>
[TestFixture]
public sealed class LlamaServerNoMmapTests
{
    // From llama-server b12128's --help (2026-11-05): --no-mmap is gone.
    private const string CurrentHelp = """
        -lm,   --load-mode MODE                 model loading mode (default: auto)
                                                - auto: mmap, unless a device does not support it
                                                - none: no special loading mode
        --rpc SERVERS                           comma-separated list of RPC servers (host:port)
        """;

    // Older builds' wording.
    private const string OlderHelp = """
        --mlock                                 force system to keep model in RAM rather than swapping or compressing
        --no-mmap                               do not memory-map model (slower load but may reduce pageouts if not using mlock)
        """;

    [Test]
    public void The_flag_is_detected_from_the_help_text()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LlamaServerManager.DetectNoMmapFlag(CurrentHelp), Is.EqualTo(NoMmap.LoadModeNone));
            Assert.That(LlamaServerManager.DetectNoMmapFlag(OlderHelp), Is.EqualTo(NoMmap.LegacyNoMmap));
            Assert.That(LlamaServerManager.DetectNoMmapFlag("usage: llama-server [options]"), Is.EqualTo(NoMmap.None));
        });
    }

    [TestCase("LoadModeNone", " --load-mode none", "--no-mmap")]
    [TestCase("LegacyNoMmap", " --no-mmap", "--load-mode")]
    public void Each_build_gets_the_flag_it_understands(string mode, string expected, string absent)
    {
        var args = new LlamaServerManager { ModelPath = "/m.gguf", NoMmapMode = Enum.Parse<NoMmap>(mode) }.BuildArgs();
        if (OperatingSystem.IsMacOS())
        {
            Assert.That(args, Does.Not.Contain("--no-mmap").And.Not.Contain("--load-mode"), "macOS keeps llama.cpp's mmap default");
            return;
        }
        Assert.That(args, Does.Contain(expected).And.Not.Contain(absent));
    }

    [Test]
    public void A_build_with_neither_flag_gets_neither()
    {
        var args = new LlamaServerManager { ModelPath = "/m.gguf", NoMmapMode = NoMmap.None }.BuildArgs();
        Assert.That(args, Does.Not.Contain("--no-mmap").And.Not.Contain("--load-mode"));
    }

    [Test]
    public void The_default_before_probing_is_the_original_no_mmap() =>
        Assert.That(new LlamaServerManager().NoMmapMode, Is.EqualTo(NoMmap.LegacyNoMmap));

    [Test]
    public void A_binary_that_cannot_be_run_keeps_the_original_flag() =>
        Assert.That(LlamaServerManager.ProbeNoMmapFlag(Path.Combine(Path.GetTempPath(), "no-such-llama-server-" + Guid.NewGuid())),
            Is.EqualTo(NoMmap.LegacyNoMmap));

    [Test]
    public void A_real_current_llama_server_is_detected_as_load_mode()
    {
        // Only where one is installed (this Mac via the installer's folder); skipped on CI.
        var dir = OperatingSystem.IsMacOS() ? MacLlamaCppDefaults.ResolveRuntimePath("") : "";
        var exe = Path.Combine(dir, "llama-server");
        if (dir.Length == 0 || !File.Exists(exe))
            Assert.Ignore("No llama-server installed here.");
        Assert.That(LlamaServerManager.ProbeNoMmapFlag(exe), Is.EqualTo(NoMmap.LoadModeNone));
    }
}
