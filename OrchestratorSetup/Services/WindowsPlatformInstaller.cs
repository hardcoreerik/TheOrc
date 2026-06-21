// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorSetup.Models;

namespace OrchestratorSetup.Services;

/// <summary>
/// INSTALLER_REVAMP_SPEC.md §7 Phase 2 — Windows implementation of <see cref="IPlatformInstaller"/>.
/// Pure delegation to the existing static services; no behavior change from before this
/// interface existed. Every one of these classes was already plain C# (never WPF), so this
/// is purely a structural move, not a rewrite.
/// </summary>
public sealed class WindowsPlatformInstaller : IPlatformInstaller
{
    // INSTALLER_REVAMP_SPEC.md §4.2 -- same values InstallerState's own field initializers
    // already use. Duplicated here (not yet wired to replace those initializers) because
    // nothing differs between the two paths until a second IPlatformInstaller implementation
    // exists to actually disagree with Windows; wiring InstallPathPage to read from here
    // instead is deferred to Phase 4, when there's a real difference to switch on.
    public string DefaultAppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "OrchestratorIDE");

    public string DefaultModelDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "OrchestratorIDE", "Models");

    public Task<HardwareDetector.HardwareInfo> DetectHardwareAsync(IProgress<string>? log, CancellationToken ct)
        => HardwareDetector.DetectAsync(log, ct);

    // Deliberately does NOT swallow exceptions (grok review MINOR, 2026-06-21: an earlier
    // version of this method caught-and-returned-false here, which silently changed
    // InstallOrchestrator's install-time HIVE step from "an Enroll exception faults the
    // step and fails the whole install" to "swallowed, install continues as if it
    // succeeded" -- a real behavior change Phase 2 isn't supposed to introduce). Letting the
    // exception propagate matches what InstallOrchestrator's call site did before this
    // interface existed (no try/catch there either). CompletePage's retry-button call site
    // is the one that originally had its own try/catch, and still does -- at that call site,
    // not duplicated in here.
    public Task<bool> ConfigureFirewallAsync(Action<string>? log, CancellationToken ct)
        => Task.Run(() => HiveEnroller.Enroll(msg => log?.Invoke(msg)), ct);

    public void CreateLaunchers(InstallerState state) => ProfileMerger.CreateShortcuts(state);

    public void RegisterUninstall(InstallerState state) => UninstallService.Register(state);

    public string? ReadInstallPath() => UninstallService.ReadInstallPath();

    public void Uninstall(string installPath, bool removeUserData, Action<string>? log)
        => UninstallService.Uninstall(installPath, removeUserData, log);

    public string LaunchCommand(string installPath) => Path.Combine(installPath, "OrchestratorIDE.exe");
}
