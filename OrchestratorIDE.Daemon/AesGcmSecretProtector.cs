// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// AES-256-GCM implementation of <see cref="ISecretProtector"/> for Linux and macOS.
/// Wire format: 12-byte random nonce | 16-byte authentication tag | ciphertext.
/// The key is loaded by <see cref="MachineKey"/> — from the THEORC_SECRET_KEY env var
/// (base-64, 32 bytes) or a machine-local key file in the config directory.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key32)
    {
        if (key32.Length != 32)
            throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key32));
        _key = key32;
    }

    public byte[] Protect(byte[] data)
    {
        var nonce  = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag    = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);

        // Layout: nonce(12) | tag(16) | ciphertext(n)
        var result = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        cipher.CopyTo(result, 28);
        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 28)
            throw new CryptographicException("Ciphertext too short.");
        var nonce  = data[..12];
        var tag    = data[12..28];
        var cipher = data[28..];
        var plain  = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
