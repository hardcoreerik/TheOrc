// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// run_shell / run_tests process construction. Windows must stay byte-identical to the original
/// PowerShell launch; macOS/Linux run bash, which used to be impossible ("powershell" doesn't
/// exist there, so every agent shell command failed at Process.Start).
/// </summary>
[TestFixture]
public sealed class ShellResolverTests
{
    // ── Windows: unchanged ──────────────────────────────────────────────────────────────

    [Test]
    public void Windows_start_info_is_identical_to_the_original_powershell_launch()
    {
        const string cmd = "Get-ChildItem | Select-Object -First 3; echo \"quotes 'mixed'\"";
        var psi = ShellResolver.CreateStartInfo(cmd, @"C:\work", ShellPlatform.Windows);

        // Exactly what ShellTools.RunAsync built before this change.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
        Assert.Multiple(() =>
        {
            Assert.That(psi.FileName, Is.EqualTo("powershell"));
            Assert.That(psi.Arguments, Is.EqualTo($"-NoProfile -NonInteractive -EncodedCommand {encoded}"));
            Assert.That(psi.ArgumentList, Is.Empty);
            Assert.That(psi.WorkingDirectory, Is.EqualTo(@"C:\work"));
            Assert.That(psi.RedirectStandardOutput, Is.True);
            Assert.That(psi.RedirectStandardError, Is.True);
            Assert.That(psi.RedirectStandardInput, Is.False);
            Assert.That(psi.UseShellExecute, Is.False);
            Assert.That(psi.CreateNoWindow, Is.True);
        });
    }

    [Test]
    public void Windows_tool_text_is_the_original_wording()
    {
        var text = ShellTools.ToolText.For(ShellPlatform.Windows);
        Assert.Multiple(() =>
        {
            Assert.That(text.Description, Is.EqualTo(
                "Run a PowerShell command in the workspace. " +
                "Use env_setup to source an environment script BEFORE the command — " +
                "both run in the same process so variables like IDF_PATH survive. " +
                "Example: env_setup='. C:\\esp-idf\\export.ps1', command='idf.py build'. " +
                "Blocked: destructive commands."));
            Assert.That(text.Command, Is.EqualTo("The PowerShell command to run."));
            Assert.That(text.EnvSetup, Is.EqualTo(
                "Optional. A PowerShell snippet run BEFORE command in the same process. " +
                "Use this to source environment scripts (e.g. '. C:\\esp-idf\\export.ps1'). " +
                "The environment it sets is visible to command."));
        });
    }

    // ── Unix: bash ──────────────────────────────────────────────────────────────────────

    [Test]
    public void Unix_start_info_is_bash_dash_c_with_the_command_as_one_argument()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Builds a Unix process; needs a Unix shell path.");

        const string cmd = "echo \"$HOME\" && ls -la | head -3";
        var psi = ShellResolver.CreateStartInfo(cmd, "/tmp", ShellPlatform.Unix);
        Assert.Multiple(() =>
        {
            Assert.That(Path.IsPathRooted(psi.FileName), Is.True, "absolute shell path, never a PATH lookup");
            Assert.That(File.Exists(psi.FileName), Is.True);
            Assert.That(psi.ArgumentList, Is.EqualTo(new[] { "-c", cmd }), "no quoting layer between policy and bash");
            Assert.That(psi.Arguments, Is.Empty);
            Assert.That(psi.RedirectStandardInput, Is.True);
        });
    }

    [Test]
    public void Unix_tool_text_tells_the_model_to_write_bash()
    {
        var text = ShellTools.ToolText.For(ShellPlatform.Unix);
        Assert.That(text.Description, Does.Contain("bash").And.Not.Contain("PowerShell"));
        Assert.That(text.Command, Does.Contain("bash"));
        Assert.That(text.EnvSetup, Does.Contain("source ./env.sh"));
    }

    [Test]
    public void MergePath_keeps_order_and_drops_duplicates_and_blanks()
    {
        var merged = ShellResolver.MergePath("/opt/homebrew/bin:/usr/bin", "/usr/bin::/bin", new[] { "/usr/local/bin", "/bin" });
        Assert.That(merged, Is.EqualTo("/opt/homebrew/bin:/usr/bin:/bin:/usr/local/bin"));
    }

    // ── Unix: real runs ─────────────────────────────────────────────────────────────────

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Runs real bash commands.");
    }

    private static Task<string> Run(string cmd) =>
        ShellTools.RunAsync(cmd, Path.GetTempPath(), CancellationToken.None);

    [Test]
    public async Task RunAsync_runs_bash_and_reports_output_and_exit_code()
    {
        RequireUnix();
        Assert.That(await Run("echo hello"), Is.EqualTo("[exit 0]\nhello"));
        Assert.That(await Run("echo partial; exit 3"), Does.StartWith("[exit 3]\npartial"));
        Assert.That(await Run("echo oops >&2"), Does.Contain("[stderr]\noops"));
    }

    [Test]
    public async Task RunAsync_passes_quotes_and_dollar_signs_through_unchanged()
    {
        RequireUnix();
        Assert.That(await Run("printf '%s|' \"a b\" '$NOT_EXPANDED' \"x'y\""),
            Is.EqualTo("[exit 0]\na b|$NOT_EXPANDED|x'y|"));
    }

    [Test, CancelAfter(15_000)]
    public async Task RunAsync_gives_commands_eof_instead_of_hanging_on_stdin()
    {
        RequireUnix();
        // `cat` with an open terminal would wait forever; with stdin closed it ends at once.
        var sw = Stopwatch.StartNew();
        Assert.That(await Run("cat; echo done"), Is.EqualTo("[exit 0]\ndone"));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void Login_path_keeps_everything_on_the_current_path()
    {
        RequireUnix();
        var path = ShellResolver.LoginPath.Value ?? "";
        var entries = path.Split(':');
        foreach (var current in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            Assert.That(entries, Does.Contain(current.Trim()));
        if (Directory.Exists("/opt/homebrew/bin"))
            Assert.That(entries, Does.Contain("/opt/homebrew/bin"), "Homebrew tools must be reachable from a Finder launch");
    }

    // ── The run_shell tool end to end (handler, policy, env_setup) ──────────────────────

    private static ToolDefinition RunShellTool(string workspace)
    {
        var registry = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
        ShellTools.Register(registry, workspace);
        Assert.That(registry.TryGet("run_shell", out var tool), Is.True);
        return tool!;
    }

    [Test]
    public async Task Run_shell_env_setup_exports_reach_the_command()
    {
        RequireUnix();
        var tool = RunShellTool(Path.GetTempPath());
        var output = await tool.Handler!(new Dictionary<string, object?>
        {
            ["env_setup"] = "export ORC_TEST_VALUE=42",
            ["command"] = "echo \"value=$ORC_TEST_VALUE\"",
        }, CancellationToken.None);
        Assert.That(output, Is.EqualTo("[exit 0]\nvalue=42"));
    }

    [Test]
    public async Task Run_shell_policy_still_blocks_destructive_bash_before_anything_runs()
    {
        RequireUnix();
        var victim = Path.Combine(Path.GetTempPath(), "orc-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(victim);
        try
        {
            var tool = RunShellTool(Path.GetTempPath());
            var output = await tool.Handler!(new Dictionary<string, object?>
            {
                ["command"] = $"rm -rf \"{victim}\"",
            }, CancellationToken.None);

            Assert.That(output, Does.StartWith("[POLICY BLOCKED]"));
            Assert.That(Directory.Exists(victim), Is.True, "the blocked command must not have run");
        }
        finally
        {
            Directory.Delete(victim, recursive: true);
        }
    }

    [Test]
    public void Run_shell_is_still_approval_gated()
    {
        Assert.That(RunShellTool(Path.GetTempPath()).RequiresApproval, Is.True);
    }
}
