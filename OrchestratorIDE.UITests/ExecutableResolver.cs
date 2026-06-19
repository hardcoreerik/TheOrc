// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.UITests;

internal static class ExecutableResolver
{
    public static string Resolve(
        string environmentVariable,
        string projectDirectoryName,
        string targetFramework,
        string executableName)
    {
        var envPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var testDir = AppDomain.CurrentDomain.BaseDirectory;
        var solutionRoot = FindSolutionRoot(testDir);
        var preferredConfiguration = DetectBuildConfiguration(testDir);
        var candidates = BuildCandidates(
                solutionRoot,
                projectDirectoryName,
                targetFramework,
                executableName,
                preferredConfiguration)
            .ToArray();

        // Pick the freshest build product among Debug/Release outputs. This
        // keeps local test runs from launching stale Release while still using
        // Release when that is the most recent swarm/capture build.
        var selected = candidates
            .Where(candidate => File.Exists(candidate.Path))
            .Select(candidate => new
            {
                Candidate = candidate,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(candidate.Path)
            })
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .ThenByDescending(item => item.Candidate.Configuration == preferredConfiguration)
            .ThenBy(item => item.Candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (selected is not null)
            return selected.Candidate.Path;

        throw new FileNotFoundException(
            $"{executableName} not found under solution root '{solutionRoot}'. " +
            $"Build the project first, or set {environmentVariable}.\n" +
            "Tried:\n  " + string.Join("\n  ", candidates.Select(c => c.Path)));
    }

    private static string FindSolutionRoot(string testDir)
    {
        var dir = new DirectoryInfo(testDir);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (dir.GetFiles("*.slnx").Length > 0)
                break;
            dir = dir.Parent;
        }

        if (dir is null)
            throw new FileNotFoundException(
                "Could not locate solution root (.slnx) while searching upward from: " + testDir);

        return dir.FullName;
    }

    private static string DetectBuildConfiguration(string testDir)
    {
        var parts = testDir.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains("Release", StringComparer.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
    }

    private static IEnumerable<ExecutableCandidate> BuildCandidates(
        string solutionRoot,
        string projectDirectoryName,
        string targetFramework,
        string executableName,
        string preferredConfiguration)
    {
        var fallbackConfiguration = preferredConfiguration == "Release" ? "Debug" : "Release";
        foreach (var configuration in new[] { preferredConfiguration, fallbackConfiguration }.Distinct())
        {
            yield return new ExecutableCandidate(
                Path.Combine(solutionRoot, projectDirectoryName, "bin", configuration, targetFramework, executableName),
                configuration);
            yield return new ExecutableCandidate(
                Path.Combine(solutionRoot, projectDirectoryName, "bin", configuration, targetFramework, "win-x64", executableName),
                configuration);
        }
    }

    private sealed record ExecutableCandidate(string Path, string Configuration);
}
