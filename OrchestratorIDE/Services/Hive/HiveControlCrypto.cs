// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Application-layer encryption for sensitive Companion control traffic.</summary>
public static class HiveControlCrypto
{
    private static readonly byte[] Info = "hive-control-v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Envelope(string Nonce, string Ciphertext, string Tag);

    public static Envelope Encrypt<T>(byte[] hiveSecret, T value)
    {
        var key = DeriveKey(hiveSecret);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Info);
        CryptographicOperations.ZeroMemory(key);
        return new(Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    public static T Decrypt<T>(byte[] hiveSecret, ReadOnlySpan<byte> json)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, Json)
            ?? throw new CryptographicException("Missing encrypted control envelope.");
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (nonce.Length != 12 || tag.Length != 16)
            throw new CryptographicException("Invalid encrypted control envelope.");

        var key = DeriveKey(hiveSecret);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Info);
            return JsonSerializer.Deserialize<T>(plaintext, Json)
                ?? throw new JsonException("Empty encrypted control payload.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(byte[] hiveSecret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, hiveSecret, 32,
            salt: "TheOrc.HIVE.Control"u8.ToArray(), info: Info);
}
