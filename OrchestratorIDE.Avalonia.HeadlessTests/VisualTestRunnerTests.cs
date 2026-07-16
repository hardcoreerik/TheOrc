// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.Windows;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Headless coverage for the visual test runner: window/control construction (loads AXAML,
/// compiles bindings, resolves brand resources), state pushes through the full telemetry
/// pipeline, lite-mode toggling, and render-without-throw for every terminal phase.
/// </summary>
[TestFixture]
public class VisualTestRunnerTests
{
    [AvaloniaTest]
    public void ChatModelBenchWindow_constructs()
        => Assert.DoesNotThrow(() => _ = new ChatModelBenchWindow(new OllamaClient()));

    [AvaloniaTest]
    public void ChatModelBenchWindow_constructs_with_settings()
        => Assert.DoesNotThrow(() => _ = new ChatModelBenchWindow(new OllamaClient(), null, new AppSettings { BenchLiteMode = true }));

    [AvaloniaTest]
    public void NeuralFlowVisualizer_renders_every_phase_without_throwing()
    {
        var viz = new NeuralFlowVisualizer();
        var window = new Window { Width = 400, Height = 300, Content = viz };
        window.Show();

        foreach (var phase in Enum.GetValues<TestRunPhase>())
        {
            viz.SetState(new NeuralFlowVisualizer.FlowState(
                phase, TotalSamples: 48, CompletedSamples: 20,
                PassedSamples: 15, WarningSamples: 2, FailedSamples: 3,
                CurrentOperation: "test", FailureSummary: null));
            viz.PulseSample(TestActivityKind.Success);
            viz.PulseSample(TestActivityKind.Failure);
            Assert.DoesNotThrow(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), $"render failed in phase {phase}");
        }
        window.Close();
    }

    [AvaloniaTest]
    public void NeuralFlowVisualizer_lite_mode_toggles_without_throwing()
    {
        var viz = new NeuralFlowVisualizer();
        var window = new Window { Width = 400, Height = 300, Content = viz };
        window.Show();

        viz.SetState(new NeuralFlowVisualizer.FlowState(
            TestRunPhase.Running, 10, 5, 4, 0, 1, "running", null));
        viz.LiteMode = true;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        viz.LiteMode = false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.Close();
    }

    [AvaloniaTest]
    public void TestStageTimeline_renders_all_statuses_and_dense_mode()
    {
        var timeline = new TestStageTimeline();
        var window = new Window { Width = 700, Height = 80, Content = timeline };
        window.Show();

        // Sparse: one stage per status.
        var model = new TestRunTelemetryModel();
        model.StartRun(
            [("a", "Init"), ("b", "Bench"), ("c", "Score"), ("d", "Report"), ("e", "Extra"), ("f", "Tail")],
            totalSamples: 10);
        model.StageStarted("a"); model.StageEnded("a", TestStageStatus.Completed);
        model.StageStarted("b"); model.StageEnded("b", TestStageStatus.Warning, "2 refused");
        model.StageStarted("c"); model.StageEnded("c", TestStageStatus.Failed, "boom");
        model.StageStarted("d");
        timeline.SetStages(model.Stages);
        Assert.DoesNotThrow(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick());

        // Dense: 30 stages must compress instead of overflowing.
        var dense = new TestRunTelemetryModel();
        dense.StartRun(Enumerable.Range(0, 30).Select(i => ($"s{i}", $"model-{i}-with-a-long-name")), 300);
        dense.StageStarted("s12", 10);
        timeline.SetStages(dense.Stages);
        Assert.DoesNotThrow(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        window.Close();
    }

    [AvaloniaTest]
    public void Timeline_and_visualizer_survive_full_telemetry_lifecycle()
    {
        // Drives the real pipeline the way ChatModelBenchWindow does: telemetry model events →
        // control state pushes, through run/pause/resume/samples/failure/end.
        var viz = new NeuralFlowVisualizer();
        var timeline = new TestStageTimeline();
        var window = new Window
        {
            Width = 600, Height = 400,
            Content = new StackPanel { Children = { viz, timeline } },
        };
        window.Show();

        var m = new TestRunTelemetryModel();
        m.Updated += () =>
        {
            viz.SetState(new NeuralFlowVisualizer.FlowState(
                m.Phase, m.TotalSamples, m.CompletedSamples,
                m.PassedSamples, m.WarningSamples, m.FailedSamples,
                m.CurrentOperation, null));
            timeline.SetStages(m.Stages);
        };

        m.StartRun([("init", "Initialize"), ("bench", "Bench"), ("report", "Report")], 6);
        m.StageStarted("init"); m.StageEnded("init", TestStageStatus.Completed);
        m.StageStarted("bench", 6);
        for (var i = 0; i < 3; i++)
        {
            m.SampleStarted($"case {i}");
            m.SampleCompleted(TestActivityKind.Success);
            viz.PulseSample(TestActivityKind.Success);
        }
        m.PauseRun();
        m.ResumeRun();
        m.SampleCompleted(TestActivityKind.Failure, feedMessage: "case 3 failed");
        viz.PulseSample(TestActivityKind.Failure);
        m.EndRun(TestRunPhase.Failed, "Bench failed: simulated");

        Assert.DoesNotThrow(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        Assert.Multiple(() =>
        {
            Assert.That(m.Stages[1].Status, Is.EqualTo(TestStageStatus.Failed));
            Assert.That(m.Stages[2].Status, Is.EqualTo(TestStageStatus.Cancelled));
            Assert.That(m.Activity, Is.Not.Empty);
        });
        window.Close();
    }

    // ── End-to-end: real window, scripted runtime (no model, no network) ──────────

    private static async Task PumpUntilAsync(Func<bool> done, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("timed out waiting for the bench run to reach the expected state");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (ChatModelBenchWindow win, string tempRoot) OpenWindowWith(ScriptedBenchRuntime runtime)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "theorc-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var win = new ChatModelBenchWindow(new OllamaClient(), tempRoot, runtime: runtime);
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (win, tempRoot);
    }

    [AvaloniaTest]
    public async Task FullRun_WithScriptedRuntime_CompletesAndReportsThroughTelemetry()
    {
        var (win, tempRoot) = OpenWindowWith(new ScriptedBenchRuntime());
        try
        {
            await PumpUntilAsync(() => win.FindControl<Button>("BtnRun")!.IsEnabled);

            win.FindControl<Button>("BtnRun")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpUntilAsync(() => win.Telemetry.Phase == TestRunPhase.Completed);

            Assert.Multiple(() =>
            {
                Assert.That(win.Telemetry.CompletedSamples, Is.EqualTo(win.Telemetry.TotalSamples));
                Assert.That(win.Telemetry.TotalSamples, Is.EqualTo(ModelBenchCorpus.AllCases.Count));
                Assert.That(win.Telemetry.Stages.All(s =>
                    s.Status is TestStageStatus.Completed or TestStageStatus.Warning), Is.True,
                    "every stage must end Completed/Warning on a normal run, got: " +
                    string.Join(", ", win.Telemetry.Stages.Select(s => $"{s.Id}={s.Status}")));
                Assert.That(win.Telemetry.Activity, Is.Not.Empty);
                Assert.That(win.Telemetry.Elapsed, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(Directory.GetFiles(Path.Combine(tempRoot, ".orc", "model-bench"), "model_bench_*.json"),
                    Has.Length.EqualTo(1), "a completed run must persist its report");
            });
        }
        finally
        {
            win.Close();
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaTest]
    public async Task Cancel_MidRun_EndsCancelledAndPreservesCompletedProgress()
    {
        var runtime = new ScriptedBenchRuntime { PerCaseDelayMs = 30 };
        var (win, tempRoot) = OpenWindowWith(runtime);
        try
        {
            await PumpUntilAsync(() => win.FindControl<Button>("BtnRun")!.IsEnabled);
            win.FindControl<Button>("BtnRun")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await PumpUntilAsync(() => win.Telemetry.CompletedSamples >= 2);
            win.FindControl<Button>("BtnCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpUntilAsync(() => win.Telemetry.Phase == TestRunPhase.Cancelled);

            Assert.Multiple(() =>
            {
                Assert.That(win.Telemetry.CompletedSamples, Is.GreaterThanOrEqualTo(2),
                    "progress made before the cancel must be preserved");
                Assert.That(win.Telemetry.CompletedSamples, Is.LessThan(win.Telemetry.TotalSamples));
                Assert.That(win.Telemetry.Stages.Any(s => s.Status == TestStageStatus.Cancelled), Is.True);
                Assert.That(win.FindControl<Button>("BtnRun")!.IsEnabled, Is.True, "Run must re-arm after cancel");
            });
        }
        finally
        {
            win.Close();
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaTest]
    public async Task PauseAndResume_MidRun_HoldsThenFinishes()
    {
        var runtime = new ScriptedBenchRuntime { PerCaseDelayMs = 10 };
        var (win, tempRoot) = OpenWindowWith(runtime);
        try
        {
            await PumpUntilAsync(() => win.FindControl<Button>("BtnRun")!.IsEnabled);
            win.FindControl<Button>("BtnRun")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await PumpUntilAsync(() => win.Telemetry.CompletedSamples >= 1);
            var pause = win.FindControl<Button>("BtnPause")!;
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpUntilAsync(() => win.Telemetry.Phase == TestRunPhase.Paused);

            // Give the in-flight case time to finish, then confirm the run is actually held.
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            var heldAt = win.Telemetry.CompletedSamples;
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Assert.That(win.Telemetry.CompletedSamples, Is.LessThanOrEqualTo(heldAt + 1),
                "at most the single in-flight case may complete after pause");

            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));   // resume
            await PumpUntilAsync(() => win.Telemetry.Phase == TestRunPhase.Completed);
            Assert.That(win.Telemetry.CompletedSamples, Is.EqualTo(win.Telemetry.TotalSamples));
        }
        finally
        {
            win.Close();
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Scripted IModelRuntime for the bench window (ContextFabricScriptedRuntime discipline):
    /// answers every prompt instantly (or after PerCaseDelayMs) with a fixed substantive string.
    /// </summary>
    private sealed class ScriptedBenchRuntime : IModelRuntime
    {
        public int PerCaseDelayMs { get; init; }

        public string RuntimeName => "ScriptedBench";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string> { "scripted-model" });

        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(2048);

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            IEnumerable<AgentMessage> history,
            IReadOnlyList<object>? tools = null,
            double temperature = 0.1,
            double? topP = null,
            int maxTokens = 4096,
            Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (PerCaseDelayMs > 0) await Task.Delay(PerCaseDelayMs, ct);
            else await Task.Yield();
            yield return "Here is a substantive scripted answer used only for UI testing.";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName, ActiveModel: "scripted-model");

        public RuntimeStats GetStats() => new(RuntimeName, ActiveModel: "scripted-model");
    }
}
