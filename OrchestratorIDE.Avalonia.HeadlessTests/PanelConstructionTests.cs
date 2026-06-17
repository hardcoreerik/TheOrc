// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Headless.NUnit;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Smoke-constructs each migrated Avalonia panel headlessly. Construction loads
/// the AXAML, compiles its bindings, and resolves every StaticResource brand
/// colour against the real App resource dictionary — so a missing resource, a
/// bad compiled binding, or a broken DataTemplate fails the test immediately.
/// </summary>
[TestFixture]
public class PanelConstructionTests
{
    [AvaloniaTest]
    public void AgentPanel_constructs()       => Assert.DoesNotThrow(() => _ = new AgentPanel());

    [AvaloniaTest]
    public void ChatPanel_constructs()         => Assert.DoesNotThrow(() => _ = new ChatPanel());

    [AvaloniaTest]
    public void FileExplorerPanel_constructs() => Assert.DoesNotThrow(() => _ = new FileExplorerPanel());

    [AvaloniaTest]
    public void SettingsPanel_constructs()     => Assert.DoesNotThrow(() => _ = new SettingsPanel(new OllamaClient()));

    [AvaloniaTest]
    public void HivePanel_constructs()         => Assert.DoesNotThrow(() => _ = new HivePanel());

    [AvaloniaTest]
    public void PitBossPanel_constructs()      => Assert.DoesNotThrow(() => _ = new PitBossPanel());

    [AvaloniaTest]
    public void SwarmBoardPanel_constructs()   => Assert.DoesNotThrow(() => _ = new SwarmBoardPanel());

    [AvaloniaTest]
    public void TrainingPitPanel_constructs()  => Assert.DoesNotThrow(() => _ = new TrainingPitPanel());

    [AvaloniaTest]
    public void ToolEditorPanel_constructs()   => Assert.DoesNotThrow(() => _ = new ToolEditorPanel());

    [AvaloniaTest]
    public void UpdatePanel_constructs()       => Assert.DoesNotThrow(() => _ = new UpdatePanel());
}
