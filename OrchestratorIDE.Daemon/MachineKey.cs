// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;

namespace OrchestratorIDE.Daemon;

/// <summary>
/// Loads the 32-byte machine key used by <see cref="OrchestratorIDE.Services.Hive.AesGcmSecretProtector"/>.
///
/// Resolution order:
///   1. <c>THEORC_SECRET_KEY</c> env var — base-64 encoded 32 bytes. Use for containers / CI.
///   2. Machine-local key file in <c>~/.config/TheOrc/machine.key</c> (auto-generated on first run).
///      The file is binary (32 random bytes) and must be protected at the OS level (chmod 600).
/// </summary>
internal static class MachineKey
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheOrc", "machine.key");

    public static byte[] Load()
    {
        var envKey = Environment.GetEnvironmentVariable("THEORC_SECRET_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            var key = Convert.FromBase64String(envKey);
            if (key.Length != 32)
                throw new InvalidOperationException(
                    "THEORC_SECRET_KEY must be exactly 32 bytes (base-64 encoded).");
            return key;
        }

        if (File.Exists(KeyPath))
        {
            var key = File.ReadAllBytes(KeyPath);
            if (key.Length == 32) return key;
            throw new InvalidOperationException(
                $"Machine key file at {KeyPath} is corrupt (expected 32 bytes, got {key.Length}).");
        }

        // First run: generate and persist a random machine key.
        var newKey = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, newKey);
        // Restrict file permissions on Unix (no-op on Windows — DPAPI is used instead).
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(KeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return newKey;
    }
}
