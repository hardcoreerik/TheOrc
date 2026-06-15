// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Platform-portable secret encryption. On Windows the default impl uses DPAPI
/// (user-scope, same machine). On Linux/macOS the daemon supplies an AES-256-GCM
/// impl keyed from a machine-local file or the THEORC_SECRET_KEY env var.
///
/// Call <see cref="SecretProtection.Initialize"/> once at startup before any HIVE
/// service tries to protect or unprotect a secret.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}
