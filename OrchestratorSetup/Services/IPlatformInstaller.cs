// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorSetup.Models;

namespace OrchestratorSetup.Services;

/// <summary>
/// INSTALLER_REVAMP_SPEC.md §4.1/§7 Phase 2 — every OS-coupled install action moves behind
/// this interface. Today only <see cref="WindowsPlatformInstaller"/> exists (Phase 2: pure
/// refactor of the existing Windows logic, no behavior change); Linux/macOS implementations
/// are Phases 4-5.
///
/// Deliberately NOT identical to the interface sketched in §4.1 of the spec in two places,
/// both because Phase 2 wraps the REAL existing Windows logic rather than redesigning it:
///   - <see cref="ConfigureFirewallAsync"/> takes no `ports` parameter — the existing
///     <see cref="HiveEnroller.Enroll"/> already knows exactly which ports to open
///     internally; threading a parameter through that nothing currently varies would be
///     speculative.
///   - It returns <c>Task&lt;bool&gt;</c>, not <c>Task&lt;string?&gt;</c> — Enroll reports
///     success/failure as a bool today, not a manual fallback command string (Windows
///     doesn't have a single canonical manual command the way Linux ufw/firewalld do). The
///     richer "return the manual command on failure" contract becomes meaningful once Phase
///     4's Linux implementation actually has one to return.
/// </summary>
public interface IPlatformInstaller
{
    /// <summary>Default app install directory for this OS (INSTALLER_REVAMP_SPEC.md §4.2).</summary>
    string DefaultAppDir { get; }

    /// <summary>Default GGUF model storage directory for this OS (§4.2).</summary>
    string DefaultModelDir { get; }

    /// <summary>Probes GPU/VRAM/CUDA/RAM. Best-effort -- never throws.</summary>
    Task<HardwareDetector.HardwareInfo> DetectHardwareAsync(IProgress<string>? log, CancellationToken ct);

    /// <summary>
    /// Opens the HIVE MIND ports, prompting for elevation as needed (UAC/sudo is expected
    /// and fine -- INSTALLER_REVAMP_SPEC.md §3/§4.3: users must not have to open ports
    /// manually). Returns true on success.
    /// </summary>
    Task<bool> ConfigureFirewallAsync(Action<string>? log, CancellationToken ct);

    /// <summary>Creates OS-appropriate launchers (.lnk / .desktop / .app or symlink).</summary>
    void CreateLaunchers(InstallerState state);

    /// <summary>Registers this install for OS-native uninstall (registry / script / manifest).</summary>
    void RegisterUninstall(InstallerState state);

    /// <summary>Reads back the install path recorded by <see cref="RegisterUninstall"/>, if any.</summary>
    string? ReadInstallPath();

    /// <summary>Removes shortcuts, optionally user data, the uninstall registration, and the
    /// install directory itself.</summary>
    void Uninstall(string installPath, bool removeUserData, Action<string>? log);

    /// <summary>The OS-correct full path to the installed app's executable.</summary>
    string LaunchCommand(string installPath);
}

/// <summary>
/// Resolves the <see cref="IPlatformInstaller"/> for the current OS. Only Windows exists
/// today (Phase 2); Linux/macOS throw <see cref="PlatformNotSupportedException"/> until
/// Phases 4-5 land rather than silently falling through to Windows-only behavior on another OS.
/// </summary>
public static class PlatformInstaller
{
    // Lazy<T> (default thread-safety mode: ExecutionAndPublication) instead of a manual
    // `_current ??= Resolve()` -- that pattern races under concurrent first access and can
    // construct more than one instance (harmless here since WindowsPlatformInstaller is
    // stateless, but not a real lazy-init contract; grok review MINOR, 2026-06-21).
    private static readonly Lazy<IPlatformInstaller> _current = new(Resolve);

    public static IPlatformInstaller Current => _current.Value;

    private static IPlatformInstaller Resolve()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatformInstaller();
        throw new PlatformNotSupportedException(
            "OrchestratorSetup does not yet have a Linux/macOS installer implementation " +
            "(INSTALLER_REVAMP_SPEC.md Phases 4-5).");
    }
}
