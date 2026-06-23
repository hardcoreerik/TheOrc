// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Services;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Headless coverage for Open Chat's persisted-system-prompt feature -- the actual fix for
/// "the model doesn't remember its name/persona across app restarts." Uses
/// OpenChatMemory.{Load,Save}SystemPrompt's storePath parameter (the same temp-file seam
/// HiveHosts/T14_HiveHostsTests already established) instead of touching this machine's
/// real persisted file.
/// </summary>
[TestFixture]
public class ChatPanelMemoryPersistenceTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private string _store = "";

    [SetUp]
    public void SetUp() => _store = Path.Combine(Path.GetTempPath(), $"chatpanel_memory_test_{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() { if (File.Exists(_store)) File.Delete(_store); }

    [AvaloniaTest]
    public void LoadPersistedMemory_withNoFileYet_leavesSystemPromptEmpty()
    {
        var panel = new ChatPanel();
        panel.LoadPersistedMemory(_store);

        Assert.That(Required<TextBox>(panel, "TbOpenSystemPrompt").Text, Is.EqualTo(""));
    }

    [AvaloniaTest]
    public void LoadPersistedMemory_readsBackAPreviouslySavedPrompt()
    {
        OpenChatMemory.SaveSystemPrompt("Your name is Zeno. Remember this across sessions.", _store);

        var panel = new ChatPanel();
        panel.LoadPersistedMemory(_store);

        Assert.That(Required<TextBox>(panel, "TbOpenSystemPrompt").Text,
            Is.EqualTo("Your name is Zeno. Remember this across sessions."));
    }

    [AvaloniaTest]
    public void EditingTheSystemPromptField_autoSavesToTheOverriddenStore()
    {
        var panel = new ChatPanel();
        panel.LoadPersistedMemory(_store);   // sets MemoryStorePathOverride to _store

        Required<TextBox>(panel, "TbOpenSystemPrompt").Text = "Your name is Zeno.";
        Dispatcher.UIThread.RunJobs();

        Assert.That(OpenChatMemory.LoadSystemPrompt(_store), Is.EqualTo("Your name is Zeno."));
    }

    [AvaloniaTest]
    public void ClickingAPreset_alsoPersists_sinceItSetsTheSameTextProperty()
    {
        var panel = new ChatPanel();
        panel.LoadPersistedMemory(_store);

        Click(Required<Button>(panel, "BtnPresetDirect"));
        Dispatcher.UIThread.RunJobs();

        var saved = OpenChatMemory.LoadSystemPrompt(_store);
        Assert.That(saved, Does.Contain("Do not add disclaimers"));
    }

    [AvaloniaTest]
    public void CorruptMemoryFile_failsClosed_toEmptyString()
    {
        File.WriteAllText(_store, "{ not valid json");

        var panel = new ChatPanel();
        panel.LoadPersistedMemory(_store);

        Assert.That(Required<TextBox>(panel, "TbOpenSystemPrompt").Text, Is.EqualTo(""));
    }
}
