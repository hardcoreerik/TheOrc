using System.Security.Cryptography;
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class HiveControlTests
{
    [Test]
    public void ControlEnvelope_RoundTrips_AndRejectsTampering()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var envelope = HiveControlCrypto.Encrypt(secret, new { command = "hostname" });
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var decoded = HiveControlCrypto.Decrypt<Dictionary<string, string>>(secret, json);
        Assert.That(decoded["command"], Is.EqualTo("hostname"));

        var tampered = envelope with { Ciphertext = Convert.ToBase64String([1, 2, 3]) };
        var tamperedJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tampered,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.That(() => HiveControlCrypto.Decrypt<Dictionary<string, string>>(secret, tamperedJson),
            Throws.InstanceOf<CryptographicException>());
    }

    [Test]
    public async Task RemoteCommand_UsesApprovalQueue_AndCapturesOutput()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), $"hive-control-{Guid.NewGuid():N}.jsonl");
        using var service = new HiveRemoteControlService(new ApprovalQueue { AutoApprove = true }, auditPath);
        var created = service.CreateCommand("mobile-test", new("Write-Output hive-control-ok"));

        HiveRemoteControlService.CommandSnapshot? snapshot = created;
        for (var attempt = 0; attempt < 50 && snapshot is not null
             && snapshot.Status is "pending" or "awaiting_approval" or "running"; attempt++)
        {
            await Task.Delay(100);
            snapshot = service.GetCommand("mobile-test", created.Id);
        }

        Assert.That(snapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.Status, Is.EqualTo("complete"));
            Assert.That(snapshot.Output, Does.Contain("hive-control-ok"));
            Assert.That(service.GetCommand("different-peer", created.Id), Is.Null);
        });
        File.Delete(auditPath);
    }
}
