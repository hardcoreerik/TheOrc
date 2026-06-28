// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.Models;

internal static class HuggingFaceAccessTokenResolver
{
    private static readonly string[] EnvVarNames =
    [
        "HUGGING_FACE_HUB_TOKEN",
        "HF_TOKEN",
        "HUGGINGFACEHUB_API_TOKEN",
    ];

    public static string? Resolve(string? explicitToken = null, AppSettings? settings = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken))
            return explicitToken.Trim();

        foreach (var name in EnvVarNames)
        {
            var fromEnv = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();
        }

        settings ??= AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(settings.HuggingFaceAccessToken))
            return settings.HuggingFaceAccessToken.Trim();

        foreach (var path in EnumerateCliTokenPaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var text = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCliTokenPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            yield break;

        yield return Path.Combine(home, ".cache", "huggingface", "token");
        yield return Path.Combine(home, ".huggingface", "token");
    }
}
