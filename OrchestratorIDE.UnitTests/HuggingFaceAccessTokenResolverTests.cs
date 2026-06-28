using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class HuggingFaceAccessTokenResolverTests
{
    private const string HfToken = "HF_TOKEN";
    private const string HfHubToken = "HUGGING_FACE_HUB_TOKEN";
    private const string HfApiToken = "HUGGINGFACEHUB_API_TOKEN";

    [SetUp]
    public void SetUp()
    {
        ClearEnv();
    }

    [TearDown]
    public void TearDown()
    {
        ClearEnv();
    }

    [Test]
    public void ExplicitToken_WinsOverEnvironmentAndSettings()
    {
        Environment.SetEnvironmentVariable(HfToken, "env-token");
        var settings = new AppSettings { HuggingFaceAccessToken = "settings-token" };

        var resolved = HuggingFaceAccessTokenResolver.Resolve("explicit-token", settings);

        Assert.That(resolved, Is.EqualTo("explicit-token"));
    }

    [Test]
    public void EnvironmentToken_WinsOverSettings()
    {
        Environment.SetEnvironmentVariable(HfHubToken, "env-token");
        var settings = new AppSettings { HuggingFaceAccessToken = "settings-token" };

        var resolved = HuggingFaceAccessTokenResolver.Resolve(null, settings);

        Assert.That(resolved, Is.EqualTo("env-token"));
    }

    [Test]
    public void SettingsToken_IsUsedWhenEnvironmentMissing()
    {
        var settings = new AppSettings { HuggingFaceAccessToken = "settings-token" };

        var resolved = HuggingFaceAccessTokenResolver.Resolve(null, settings);

        Assert.That(resolved, Is.EqualTo("settings-token"));
    }

    private static void ClearEnv()
    {
        Environment.SetEnvironmentVariable(HfToken, null);
        Environment.SetEnvironmentVariable(HfHubToken, null);
        Environment.SetEnvironmentVariable(HfApiToken, null);
    }
}
