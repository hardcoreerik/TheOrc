// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class WelcomePage : UserControl, IInstallerPage
{
    public WelcomePage(InstallerViewModel vm) => InitializeComponent();

    public bool CanLeave() => true;
}
