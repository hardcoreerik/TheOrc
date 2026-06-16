// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;

namespace OrchestratorIDE;

/// <summary>
/// Phase 0 scaffold — bare window.  Service wiring, panel orchestration, and
/// HIVE startup are added incrementally in Phase 5 (MainWindow migration).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
