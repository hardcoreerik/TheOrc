// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Models;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Behavior-level smoke tests for reusable Avalonia controls. These sit between
/// construction-only tests and full UI automation: real AXAML is loaded, then
/// the code-behind state transitions are exercised headlessly.
/// </summary>
[TestFixture]
public class ControlBehaviorTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static List<T> Items<T>(ItemsControl control)
        => (control.ItemsSource as IEnumerable ?? control.Items)
            .Cast<T>()
            .ToList();

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void CommandPalette_sorts_commands_and_filters_by_keyword()
    {
        var palette = new CommandPalette();
        palette.RegisterCommands(
        [
            new PaletteCommand { Id = "settings", Label = "Settings", Detail = "Configure app", SortOrder = 20, Keywords = ["config"] },
            new PaletteCommand { Id = "open",     Label = "Open",     Detail = "Open folder",   SortOrder = 10, Keywords = ["folder"] },
            new PaletteCommand { Id = "run",      Label = "Run",      Detail = "Start task",    SortOrder = 30, Keywords = ["execute"] },
        ]);

        var list = Required<ListBox>(palette, "ResultsList");
        Assert.That(Items<PaletteCommand>(list).Select(c => c.Id), Is.EqualTo(new[] { "open", "settings", "run" }));
        Assert.That(list.SelectedIndex, Is.EqualTo(0));

        Required<TextBox>(palette, "SearchBox").Text = "config";
        Dispatcher.UIThread.RunJobs();

        var filtered = Items<PaletteCommand>(list);
        Assert.Multiple(() =>
        {
            Assert.That(filtered.Select(c => c.Id), Is.EqualTo(new[] { "settings" }));
            Assert.That(list.SelectedItem, Is.SameAs(filtered[0]));
            Assert.That(Required<TextBlock>(palette, "SearchPlaceholder").IsVisible, Is.False);
        });
    }

    [AvaloniaTest]
    public void ModelPickerPopup_orders_preferred_models_filters_embeddings_and_marks_active()
    {
        var picker = new ModelPickerPopup();
        picker.Load(
        [
            "zeta-custom:latest",
            "nomic-embed-text:latest",
            "qwen2.5-coder:7b",
            "alpha-custom:latest",
        ], "alpha-custom:latest");

        var list  = Required<ListBox>(picker, "ModelList");
        var items = Items<ModelItemVm>(list);

        Assert.Multiple(() =>
        {
            Assert.That(Required<TextBlock>(picker, "TbInstalled").Text, Is.EqualTo("4 installed"));
            Assert.That(items.Select(i => i.ModelId), Is.EqualTo(new[]
            {
                "qwen2.5-coder:7b",
                "alpha-custom:latest",
                "zeta-custom:latest",
            }));
            Assert.That(items.Any(i => i.ModelId.Contains("embed", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(items.Single(i => i.ModelId == "alpha-custom:latest").ActiveDot, Is.EqualTo("●"));
        });
    }

    [AvaloniaTest]
    public void DiffViewer_loads_semantic_diff_and_raises_decision_events()
    {
        var viewer = new DiffViewer();
        viewer.Load("src/app.cs", "one\nold\nthree", "one\nnew\nthree", "review before apply");

        var decisions = new List<string>();
        viewer.Approved += () => decisions.Add("approved");
        viewer.Rejected += () => decisions.Add("rejected");

        Click(Required<Button>(viewer, "BtnReject"));
        Click(Required<Button>(viewer, "BtnApprove"));

        var lines = Items<DiffLineVm>(Required<ItemsControl>(viewer, "DiffLines"));
        Assert.Multiple(() =>
        {
            Assert.That(Required<TextBlock>(viewer, "TbFilePath").Text, Is.EqualTo("src/app.cs"));
            Assert.That(Required<TextBlock>(viewer, "TbStats").Text, Is.EqualTo("+1 / -1"));
            Assert.That(Required<TextBlock>(viewer, "TbReason").Text, Does.Contain("review before apply"));
            Assert.That(lines.Any(l => l.Gutter == "-" && l.Text == "old"), Is.True);
            Assert.That(lines.Any(l => l.Gutter == "+" && l.Text == "new"), Is.True);
            Assert.That(decisions, Is.EqualTo(new[] { "rejected", "approved" }));
        });
    }

    [AvaloniaTest]
    public void ShellApprovalCard_populates_request_details_and_only_resolves_on_button_click()
    {
        var call = new ToolCall
        {
            Name = "run_shell",
            Arguments =
            {
                ["command"] = "dotnet test",
                ["path"] = "F:/Ai/OrchestratorIDE-dev",
            },
            ExplainWhy = "verify the headless suite",
        };
        var card = new ShellApprovalCard(call);

        var decisions = new List<bool>();
        card.Resolved += decisions.Add;

        Assert.Multiple(() =>
        {
            Assert.That(Required<TextBlock>(card, "TbToolName").Text, Is.EqualTo("run_shell"));
            Assert.That(Required<TextBlock>(card, "TbReason").Text, Is.EqualTo("verify the headless suite"));
            Assert.That(Required<StackPanel>(card, "ReasonPanel").IsVisible, Is.True);
            Assert.That(Items<object>(Required<ItemsControl>(card, "ArgsList")), Has.Count.EqualTo(2));
            Assert.That(decisions, Is.Empty);
        });

        Click(Required<Button>(card, "BtnReject"));
        Click(Required<Button>(card, "BtnApprove"));
        Assert.That(decisions, Is.EqualTo(new[] { false, true }));
    }

    [AvaloniaTest]
    public void UnknownToolCard_shows_call_preview_and_returns_safe_guidance()
    {
        var call = new ToolCall
        {
            Name = "create_project",
            Arguments =
            {
                ["project_name"] = "SampleApp",
                ["project_type"] = "console",
            },
        };
        var card = new UnknownToolCard(call, ["read_file", "write_file", "run_shell"]);

        var results = new List<string>();
        card.Resolved += results.Add;

        Assert.Multiple(() =>
        {
            Assert.That(Required<TextBlock>(card, "TbToolName").Text, Is.EqualTo("create_project"));
            Assert.That(Required<TextBlock>(card, "TbCallPreview").Text, Is.EqualTo("create_project(project_name, project_type)"));
            Assert.That(Required<TextBlock>(card, "TbArgs").Text, Does.Contain("project_name"));
            Assert.That(Required<Button>(card, "BtnImplement").IsEnabled, Is.False);
            Assert.That(results, Is.Empty);
        });

        Click(Required<Button>(card, "BtnAutoTranslate"));
        Click(Required<Button>(card, "BtnSkip"));

        Assert.Multiple(() =>
        {
            Assert.That(results[0], Does.Contain("[Tool not found: create_project]"));
            Assert.That(results[0], Does.Contain("dotnet new console -n SampleApp"));
            Assert.That(results[1], Does.Contain("was skipped by the user"));
        });
    }
}
