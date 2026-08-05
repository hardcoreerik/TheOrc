// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OrchestratorIDE.Tools;

namespace OrchestratorIDE.Core;

// ── Manifest schema ─────────────────────────────────────────────────────────
// docs/NATIVE_RUNTIME_V2_SPEC.md Phase D. A skill is a folder — {workspaceRoot}/.orc/skills/
// {name}/ — containing SKILL.md (instructions for the model, not parsed here) and tools.json
// (this schema, the machine-readable half). Unlike ToolCompiler's ICustomTool (arbitrary C#),
// a skill is declarative: name/params/method/path/auth, and SkillLoader builds a generic
// HTTP-calling ToolDefinition for each entry. Intentionally the same shape the hand-written
// integrations (ArtForgeTools.cs etc.) build by hand, just data-driven instead of compiled in.

public sealed record SkillToolParam(
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("type")] string Type = "string",
    [property: JsonPropertyName("description")] string Description = "",
    [property: JsonPropertyName("required")] bool Required = false);

public sealed record SkillToolEntry(
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("description")] string Description = "",
    [property: JsonPropertyName("method")] string Method = "",
    [property: JsonPropertyName("path")] string Path = "",
    [property: JsonPropertyName("params")] IReadOnlyList<SkillToolParam>? Params = null,
    // Defaults to true (fail-toward-caution) when a manifest entry omits it, matching the
    // hand-written integrations' posture: an omitted approval flag must never silently mean
    // "runs unattended."
    [property: JsonPropertyName("requires_approval")] bool RequiresApproval = true);

public sealed record SkillAuth(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("token_env")] string? TokenEnv = null);

public sealed record SkillManifest(
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("base_url_env")] string? BaseUrlEnv = null,
    [property: JsonPropertyName("base_url_default")] string BaseUrlDefault = "",
    [property: JsonPropertyName("auth")] SkillAuth? Auth = null,
    [property: JsonPropertyName("tools")] IReadOnlyList<SkillToolEntry>? Tools = null);

// ── Loader ────────────────────────────────────────────────────────────────

/// <summary>
/// Scans {workspaceRoot}/.orc/skills/*/tools.json and registers a generic HTTP-calling
/// ToolDefinition per declared entry — the manifest-driven counterpart to ToolCompiler's
/// compiled-C# tools, same auto-load-on-workspace-open convention
/// (see MainWindow.AutoLoadWorkspaceToolsAsync, which calls both).
///
/// Known, deliberate scope reduction vs. the hand-written integrations (ArtForgeTools.cs etc.):
/// no per-field regex validation (job-ID patterns, size patterns, ...), only required-field
/// presence and basic type coercion. A skill trades some bespoke input safety for genericity;
/// callers that need tighter validation should still hand-write a ToolDefinition.
/// </summary>
public static class SkillLoader
{
    private const int MaxResponseBytes = 1_000_000;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Mirrors ToolCompiler.ScanAndLoadAllAsync's return shape for the same Activity-log
    /// reporting pattern. A malformed skill is skipped and reported, never throws past this
    /// point — one bad manifest must not block every other skill or crash workspace-open.
    /// </summary>
    public static async Task<List<(string File, bool Ok, string? Error)>> ScanAndLoadAllAsync(
        ToolRegistry registry, string workspaceRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var dir = Path.Combine(workspaceRoot, ".orc", "skills");
        if (!Directory.Exists(dir)) return [];

        var results = new List<(string, bool, string?)>();
        foreach (var manifestPath in Directory.GetFiles(dir, "tools.json", SearchOption.AllDirectories).OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            var label = Path.GetRelativePath(dir, manifestPath);
            try
            {
                var (ok, error) = await LoadOneAsync(registry, manifestPath, ct);
                results.Add((label, ok, error));
            }
            catch (Exception ex)
            {
                results.Add((label, false, ex.Message));
            }
        }
        return results;
    }

    private static async Task<(bool Ok, string? Error)> LoadOneAsync(
        ToolRegistry registry, string manifestPath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        SkillManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SkillManifest>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}");
        }
        if (manifest is null)
            return (false, "Manifest deserialized to null.");
        if (string.IsNullOrWhiteSpace(manifest.Name))
            return (false, "Missing 'name'.");
        if (string.IsNullOrWhiteSpace(manifest.BaseUrlDefault) ||
            !Uri.TryCreate(manifest.BaseUrlDefault, UriKind.Absolute, out _))
            return (false, $"'base_url_default' must be an absolute URL, got: '{manifest.BaseUrlDefault}'.");

        var resolvedBaseUrl = manifest.BaseUrlEnv is { Length: > 0 } envName
            && Environment.GetEnvironmentVariable(envName) is { Length: > 0 } envValue
                ? envValue
                : manifest.BaseUrlDefault;
        if (!Uri.TryCreate(resolvedBaseUrl, UriKind.Absolute, out var baseUri))
            return (false, $"Resolved base URL is not absolute: '{resolvedBaseUrl}'.");
        // Same restriction every hand-written local integration enforces (LocalIntegrationHost,
        // PR #96) — a skill can never point a caller at a public host, whether or not it
        // declares a bearer token.
        if (!LocalIntegrationHost.IsLocal(baseUri))
            return (false,
                $"Skill '{manifest.Name}' base URL must be a loopback, private IP, or single-label " +
                $"LAN host: '{baseUri}'.");

        var tools = manifest.Tools ?? [];
        if (tools.Count == 0)
            return (false, "No tools declared.");

        var token = manifest.Auth?.TokenEnv is { Length: > 0 } tokenEnv
            ? Environment.GetEnvironmentVariable(tokenEnv)
            : null;
        var http = LocalIntegrationHost.CreateClient(token, baseUri);
        var apiBase = baseUri.ToString().TrimEnd('/');

        var registeredCount = 0;
        var skipped = new List<string>();
        foreach (var entry in tools)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Description)
                || entry.Method is not ("GET" or "POST") || string.IsNullOrWhiteSpace(entry.Path))
            {
                skipped.Add(string.IsNullOrWhiteSpace(entry.Name) ? "(unnamed)" : entry.Name);
                continue;
            }

            var boundEntry = entry;
            registry.Register(new ToolDefinition
            {
                Name = boundEntry.Name,
                Description = boundEntry.Description,
                Parameters = (boundEntry.Params ?? []).ToDictionary(
                    p => p.Name, p => new ToolParameter(p.Type, p.Description)),
                Required = (boundEntry.Params ?? []).Where(p => p.Required).Select(p => p.Name).ToArray(),
                RequiresApproval = boundEntry.RequiresApproval,
                Handler = (args, innerCt) => InvokeAsync(http, apiBase, boundEntry, args, innerCt),
            });
            registeredCount++;
        }

        if (registeredCount == 0)
            return (false, $"Every declared tool was invalid: {string.Join(", ", skipped)}");

        return skipped.Count == 0
            ? (true, null)
            : (true, $"Registered {registeredCount}, skipped {skipped.Count} invalid " +
                     $"entr{(skipped.Count == 1 ? "y" : "ies")}: {string.Join(", ", skipped)}");
    }

    // Matches {name} path-template tokens, e.g. "/v1/jobs/{job_id}" -> group "job_id".
    private static readonly Regex PathParamPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    private static async Task<string> InvokeAsync(HttpClient http, string apiBase, SkillToolEntry entry,
        Dictionary<string, object?> args, CancellationToken ct)
    {
        foreach (var required in (entry.Params ?? []).Where(p => p.Required))
            if (!args.TryGetValue(required.Name, out var value) || value is null)
                throw new ArgumentException($"{required.Name} is required.");

        // A declared path like "/v1/jobs/{job_id}" is used verbatim by every OTHER field in this
        // class (it's just a string), but the literal token must never reach the HTTP layer -- a
        // request for the literal path "/v1/jobs/%7Bjob_id%7D" would 404 while the skill still
        // LOADS cleanly, i.e. fail silently at call time instead of at load time (found live
        // authoring TheOrc's own CaseForge skill, docs/NATIVE_RUNTIME_V2_SPEC.md Phase E: job
        // status/cancel are inexpressible without this). Substituted values are also excluded
        // from the query string / JSON body below -- they belong in the path, not the payload.
        var resolvedPath = SubstitutePathParams(entry.Path, args, out var consumedByPath);
        var url = $"{apiBase}{resolvedPath}";
        var payloadArgs = consumedByPath.Count == 0
            ? args
            : args.Where(kv => !consumedByPath.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        using var request = entry.Method == "GET"
            ? new HttpRequestMessage(HttpMethod.Get, BuildQueryUrl(url, payloadArgs))
            : new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payloadArgs), Encoding.UTF8, "application/json"),
            };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            return $"[ERROR] {entry.Name} response exceeded 1 MB.";
        var text = await response.Content.ReadAsStringAsync(ct);
        if (Encoding.UTF8.GetByteCount(text) > MaxResponseBytes)
            return $"[ERROR] {entry.Name} response exceeded 1 MB.";
        return response.IsSuccessStatusCode
            ? text
            : $"[ERROR] {entry.Name} returned HTTP {(int)response.StatusCode}: {text}";
    }

    /// <summary>
    /// Replaces every {name} token in a declared path with the URL-escaped value of the
    /// matching argument. <paramref name="consumed"/> carries back which argument keys were
    /// used in the path, so the caller can exclude them from the query string / JSON body --
    /// same failure shape as the pre-existing required-param check (ArgumentException) when a
    /// token has no matching argument, since by this point that same check would already have
    /// fired for any path param the manifest also declared as required in `params[]`.
    /// </summary>
    private static string SubstitutePathParams(
        string path, Dictionary<string, object?> args, out HashSet<string> consumed)
    {
        var consumedNames = new HashSet<string>();
        var resolved = PathParamPattern.Replace(path, match =>
        {
            var name = match.Groups[1].Value;
            if (!args.TryGetValue(name, out var value) || value is null)
                throw new ArgumentException($"{name} is required.");
            consumedNames.Add(name);
            return Uri.EscapeDataString(value.ToString() ?? "");
        });
        consumed = consumedNames;
        return resolved;
    }

    private static string BuildQueryUrl(string url, Dictionary<string, object?> args)
    {
        var query = string.Join("&", args
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!.ToString() ?? "")}"));
        return query.Length > 0 ? $"{url}?{query}" : url;
    }
}
