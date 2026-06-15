// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Windows DPAPI implementation of <see cref="ISecretProtector"/>.
/// Secrets are encrypted to the current user account on the local machine —
/// the same semantics as the previous inline <c>ProtectedData</c> calls.
/// Only compiled into the WPF project (which carries the ProtectedData NuGet ref).
/// </summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data)
        => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data)
        => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
