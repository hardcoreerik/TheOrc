// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrchestratorIDE.Core;
using OrchestratorIDE.UI.Panels;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.UI.Windows;

public partial class SelfUpdateWindow : Window
{
    private static SelfUpdateWindow? _instance;
    private AppSettings? _settings;

    public SelfUpdateWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += (_, _) => _instance = null;
    }

    public static void ShowWindow(Window owner, AppSettings settings)
    {
        if (_instance is null || !_instance.IsVisible)
        {
            _instance = new SelfUpdateWindow();
            _instance._settings = settings;
            _instance.Show(owner);
        }
        else
        {
            _instance._settings = settings;
        }

        _instance.ApplySettingsAndRefresh();
        _instance.Activate();
    }

    private void OnOpened(object? sender, EventArgs e)
        => ApplySettingsAndRefresh();

    private void ApplySettingsAndRefresh()
    {
        if (_settings is null)
            return;

        UpdatePanelHost.Settings = _settings;
        UpdatePanelHost.IsWarchief = false;
        UpdatePanelHost.LocalNodeId = "";
        UpdatePanelHost.ConfirmAsync = (message, title) => DialogHelper.ShowYesNoAsync(this, title, message);
        UpdatePanelHost.Refresh();
    }
}
