using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T14 — HIVE MIND Phase A host store (pure logic, temp-file backed).
/// </summary>
[TestFixture]
public class T14_HiveHostsTests
{
    private string _store = "";

    [SetUp]
    public void SetUp() => _store = Path.Combine(Path.GetTempPath(),
        $"hive_test_{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() { if (File.Exists(_store)) File.Delete(_store); }

    [Test]
    public void Load_EmptyStore_SeedsThisPcFirst()
    {
        var hosts = HiveHosts.Load("http://localhost:11434", _store);

        Assert.That(hosts[0].Name, Is.EqualTo("This PC"));
        Assert.That(hosts[0].Url, Is.EqualTo("http://localhost:11434"));
    }

    [Test]
    public void SaveLoad_RoundTrips_AndThisPcUrlTracksSettings()
    {
        var hosts = HiveHosts.Load("http://localhost:11434", _store);
        hosts.Add(new HiveHost { Name = "HARDCOREPC", Url = "http://192.168.1.20:11434" });
        HiveHosts.Save(hosts, _store);

        // Reload with a CHANGED local url — "This PC" must follow settings,
        // remote entries must persist.
        var again = HiveHosts.Load("http://127.0.0.1:9999", _store);

        Assert.Multiple(() =>
        {
            Assert.That(again.Count, Is.EqualTo(2));
            Assert.That(again.First(h => h.Name == "This PC").Url,
                        Is.EqualTo("http://127.0.0.1:9999"));
            Assert.That(again.Any(h => h.Name == "HARDCOREPC"), Is.True);
        });
    }

    [Test]
    public void Load_CorruptStore_StartsFreshInsteadOfThrowing()
    {
        File.WriteAllText(_store, "{not json[");
        var hosts = HiveHosts.Load(null, _store);
        Assert.That(hosts, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Probe_UnreachableHost_IsStateNotError()
    {
        var host = new HiveHost { Name = "ghost", Url = "http://192.0.2.1:11434" };
        await HiveHosts.ProbeAsync(host, timeoutSeconds: 1);

        Assert.That(host.Reachable, Is.False);
        Assert.That(host.Models, Is.Empty);
    }
}
