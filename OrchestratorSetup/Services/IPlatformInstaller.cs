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
/// Deliberately NOT identical to the interface sketched in §4.1 of the spec in a few places,
/// each because a real implementation needed something the original sketch didn't have yet:
///   - <see cref="ConfigureFirewallAsync"/> takes no `ports` parameter — the existing
///     <see cref="HiveEnroller.Enroll"/> already knows exactly which ports to open
///     internally; threading a parameter through that nothing currently varies would be
///     speculative.
///   - It returns <c>Task&lt;bool&gt;</c>, not <c>Task&lt;string?&gt;</c> — Enroll reports
///     success/failure as a bool today, not a manual fallback command string (Windows
///     doesn't have a single canonical manual command the way Linux ufw/firewalld do). The
///     richer "return the manual command on failure" contract becomes meaningful once a
///     Linux/macOS implementation actually has one to return.
///   - It DOES take an `appExePath` parameter, added in Phase 5 — macOS's Application
///     Firewall is per-app, not per-port (§4.3), so <see cref="MacPlatformInstaller"/>
///     genuinely needs to know which binary to allow-list. Windows and Linux both ignore it
///     (grok review BLOCKER, 2026-06-21: an earlier version of MacPlatformInstaller resolved
///     the path itself via <c>LaunchCommand(DefaultAppDir)</c>, which silently mismatched any
///     non-default install path the user picked on InstallPathPage).
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
    /// Opens the HIVE MIND ports, prompting for elevation as needed (UAC/sudo/pkexec is
    /// expected and fine -- INSTALLER_REVAMP_SPEC.md §3/§4.3: users must not have to open
    /// ports manually). <paramref name="appExePath"/> is the actual installed binary's full
    /// path -- only macOS's per-app firewall needs it; Windows/Linux ignore it. Returns true
    /// on success.
    /// </summary>
    Task<bool> ConfigureFirewallAsync(string appExePath, Action<string>? log, CancellationToken ct);

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
/// Resolves the <see cref="IPlatformInstaller"/> for the current OS. Windows, Linux, and
/// macOS all exist now (Phases 2, 4, 5); any other OS throws
/// <see cref="PlatformNotSupportedException"/> rather than silently falling through to
/// another OS's behavior.
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
        if (OperatingSystem.IsLinux())   return new LinuxPlatformInstaller();
        if (OperatingSystem.IsMacOS())   return new MacPlatformInstaller();
        throw new PlatformNotSupportedException(
            "OrchestratorSetup does not support this operating system.");
    }
}
