// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
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
}
