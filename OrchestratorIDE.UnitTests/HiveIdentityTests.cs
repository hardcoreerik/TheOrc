// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// HiveIdentity.Load's regenerateOnCorruption default flip (false as of the 2026-07-29
/// NewcorePC Warchief-identity incident) was, until now, only exercised indirectly through the
/// Daemon/SwarmCli --show-identity try/catch wiring -- Load() itself is a process-wide static
/// singleton (_instance cached for the process lifetime, IdentityPath pointing at the real
/// %AppData% location) with no reset hook, which made the throw-vs-regenerate decision itself
/// structurally hard to unit-test directly (flagged as an open gap in
/// NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md's round-10 entry). LoadFromPathForTest closes that
/// gap: it's the same decrypt/parse/throw-or-regenerate decision, factored out against an
/// arbitrary path with no singleton or disk-write side effects, so it's safe to call from any
/// test without touching the real identity file or leaking state into other tests.
/// </summary>
[TestFixture]
public sealed class HiveIdentityTests
{
    private string _tempDir = "";
    private string _identityPath = "";

    /// <summary>Same idempotent init as HiveAuthSignRoundTripTests -- production initializes this
    /// at startup (GUI: DPAPI), a test host never does, and Initialize is a plain assignment so
    /// this does not fight any other fixture that also sets it.</summary>
    [OneTimeSetUp]
    public void InitSecretProtection() =>
        SecretProtection.Initialize(new DpapiSecretProtector());

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hive-identity-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _identityPath = Path.Combine(_tempDir, "hive-identity.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void LoadFromPathForTest_NoFileAtAll_CreatesFreshIdentity_RegardlessOfFlag()
    {
        // A missing file is a genuinely new node, not corruption -- Load's own doc comment says
        // this must create a fresh identity regardless of regenerateOnCorruption.
        using var identity = HiveIdentity.LoadFromPathForTest(_identityPath, regenerateOnCorruption: false);

        Assert.That(identity.NodeId, Is.Not.Empty);
    }

    [Test]
    public void LoadFromPathForTest_CorruptFile_RegenerateOnCorruptionFalse_Throws()
    {
        File.WriteAllBytes(_identityPath, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        Assert.That(
            () => HiveIdentity.LoadFromPathForTest(_identityPath, regenerateOnCorruption: false),
            Throws.Exception,
            "the strict (new default) path must refuse to silently replace an unreadable identity");
    }

    [Test]
    public void LoadFromPathForTest_CorruptFile_RegenerateOnCorruptionTrue_ReturnsFreshIdentity()
    {
        File.WriteAllBytes(_identityPath, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        HiveIdentity? identity = null;
        Assert.That(
            () => identity = HiveIdentity.LoadFromPathForTest(_identityPath, regenerateOnCorruption: true),
            Throws.Nothing,
            "the opt-in legacy path must silently regenerate instead of throwing");
        Assert.That(identity!.NodeId, Is.Not.Empty);
        identity.Dispose();
    }

    [Test]
    public void LoadFromPathForTest_FileDeserializesToLiteralNull_RegenerateOnCorruptionFalse_ThrowsInvalidOperationException()
    {
        // "null" is valid JSON that deserializes to a null StoredIdentity without throwing during
        // parsing -- Load's own comment calls out this is the same "existing file, unusable
        // content" case as a genuine decrypt/parse failure, just reached without an exception,
        // so it needs its own explicit throw. Written directly through the same DPAPI protection
        // DpapiLoad expects, via a real Persist-then-overwrite round trip, would require reaching
        // into SecretProtection -- simpler to confirm this exact branch's own explicit exception
        // type and message instead of the generic corrupt-bytes case above.
        var freshPath = Path.Combine(_tempDir, "fresh-then-nulled.json");
        using (var seed = HiveIdentity.LoadFromPathForTest(freshPath, regenerateOnCorruption: false))
            Assert.That(seed.NodeId, Is.Not.Empty, "sanity: seed identity constructed fine");

        // LoadFromPathForTest never persists (see its own doc comment) -- reaching the
        // deserializes-to-null branch means writing a file whose decrypted content is literally
        // "null", which needs actual DPAPI protection over that exact plaintext.
        var protectedNull = SecretProtection.Current
            .Protect(System.Text.Encoding.UTF8.GetBytes("null"));
        File.WriteAllBytes(_identityPath, protectedNull);

        var ex = Assert.Throws<InvalidOperationException>(
            () => HiveIdentity.LoadFromPathForTest(_identityPath, regenerateOnCorruption: false));
        Assert.That(ex!.Message, Does.Contain("Refusing to silently generate a replacement identity"));
    }
}
