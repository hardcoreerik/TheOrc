// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Process-wide <see cref="ISecretProtector"/> singleton. Call <see cref="Initialize"/>
/// once at process startup (before any HIVE service accesses a secret).
///
/// WPF app: <c>SecretProtection.Initialize(new DpapiSecretProtector());</c><br/>
/// Daemon:  <c>SecretProtection.Initialize(new AesGcmSecretProtector(MachineKey.Load()));</c>
/// </summary>
public static class SecretProtection
{
    private static ISecretProtector? _current;

    public static ISecretProtector Current
        => _current ?? throw new InvalidOperationException(
               "SecretProtection.Initialize() must be called before HIVE secrets are accessed.");

    public static void Initialize(ISecretProtector protector)
        => _current = protector;
}
