// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class KeyHoundAtlasTools
{
    private const int MaxJsonBytes = 2_000_000;
    private static readonly Regex RunIdPattern = new("^run_[0-9a-f]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex EntityIdPattern = new("^ent_[0-9a-f]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex LinkIdPattern = new("^lnk_[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    public static void Register(ToolRegistry registry, Uri serviceUrl, string bearerToken,
        Uri? workspaceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!IsLocal(serviceUrl))
            throw new ArgumentException("KeyHound Atlas must use a loopback, private IP, or single-label LAN host.", nameof(serviceUrl));
        if (workspaceUrl is not null && !IsLocal(workspaceUrl))
            throw new ArgumentException("The KeyHound Atlas workspace must be local.", nameof(workspaceUrl));
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Length < 32)
            throw new ArgumentException("A KeyHound Atlas integration token of at least 32 characters is required.", nameof(bearerToken));

        var http = LocalIntegrationHost.CreateClient(bearerToken);
        var api = serviceUrl.ToString().TrimEnd('/');
        var editor = (workspaceUrl ?? serviceUrl).ToString().TrimEnd('/');

        registry.Register(new ToolDefinition
        {
            Name = "atlas_start",
            Description = "Start a KeyHound Atlas evidence-graph run for a target. This may initiate network research.",
            Parameters = new() { ["target"] = new("string", "Domain, handle, email, phone, IP, or other research target") },
            Required = ["target"],
            RequiresApproval = true,
            Handler = async (args, ct) =>
            {
                var target = RequiredString(args, "target");
                if (target.Length > 500) throw new ArgumentException("target must contain no more than 500 characters.");
                using var content = JsonContent(new Dictionary<string, object?> { ["target"] = target });
                return await SendJsonAsync(http,
                    new HttpRequestMessage(HttpMethod.Post, $"{api}/api/pepe-core/runs") { Content = content }, ct);
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "atlas_graph",
            Description = "Read an existing KeyHound Atlas evidence graph.",
            Parameters = new() { ["run_id"] = new("string", "KeyHound Atlas run identifier") },
            Required = ["run_id"],
            Handler = async (args, ct) => await SendJsonAsync(http,
                new HttpRequestMessage(HttpMethod.Get, $"{api}/api/pepe-core/graph/{RunId(args)}"), ct),
        });

        registry.Register(new ToolDefinition
        {
            Name = "atlas_expand",
            Description = "Expand one entity in an existing KeyHound Atlas evidence graph. This may initiate network research.",
            Parameters = new()
            {
                ["run_id"] = new("string", "KeyHound Atlas run identifier"),
                ["entity_id"] = new("string", "Entity identifier from atlas_graph"),
            },
            Required = ["run_id", "entity_id"],
            RequiresApproval = true,
            Handler = async (args, ct) =>
            {
                using var content = JsonContent(new Dictionary<string, object?>
                {
                    ["run_id"] = RunId(args),
                    ["entity_id"] = Id(args, "entity_id", EntityIdPattern),
                });
                return await SendJsonAsync(http,
                    new HttpRequestMessage(HttpMethod.Post, $"{api}/api/pepe-core/expand") { Content = content }, ct);
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "atlas_evidence",
            Description = "Read the evidence and provenance supporting one KeyHound Atlas graph link.",
            Parameters = new() { ["link_id"] = new("string", "Link identifier from atlas_graph") },
            Required = ["link_id"],
            Handler = async (args, ct) => await SendJsonAsync(http,
                new HttpRequestMessage(HttpMethod.Get,
                    $"{api}/api/pepe-core/evidence/{Id(args, "link_id", LinkIdPattern)}"), ct),
        });

        registry.Register(new ToolDefinition
        {
            Name = "atlas_open",
            Description = "Open KeyHound Atlas to a research target.",
            Parameters = new() { ["target"] = new("string", "Optional target to open in the PEPE graph workspace") },
            RequiresApproval = true,
            Handler = (args, _) =>
            {
                var target = String(args, "target");
                if (target.Length > 500) throw new ArgumentException("target must contain no more than 500 characters.");
                var url = string.IsNullOrWhiteSpace(target)
                    ? $"{editor}/pepe"
                    : $"{editor}/pepe?target={Uri.EscapeDataString(target)}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Task.FromResult($"Opened KeyHound Atlas: {url}");
            },
        });
    }

    private static StringContent JsonContent(Dictionary<string, object?> body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<string> SendJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.Content.Headers.ContentLength > MaxJsonBytes)
                return "[ERROR] KeyHound Atlas response exceeded 2 MB.";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            while (buffer.Length <= MaxJsonBytes)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length > MaxJsonBytes)
                return "[ERROR] KeyHound Atlas response exceeded 2 MB.";
            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            return response.IsSuccessStatusCode
                ? text
                : $"[ERROR] KeyHound Atlas returned HTTP {(int)response.StatusCode}: {text}";
        }
    }

    private static bool IsLocal(Uri uri) => LocalIntegrationHost.IsLocal(uri);

    private static string RunId(Dictionary<string, object?> args) => Id(args, "run_id", RunIdPattern);

    private static string Id(Dictionary<string, object?> args, string key, Regex pattern)
    {
        var id = RequiredString(args, key);
        return pattern.IsMatch(id) ? id : throw new ArgumentException($"{key} has an invalid KeyHound Atlas identifier.");
    }

    private static string RequiredString(Dictionary<string, object?> args, string key)
    {
        var value = String(args, key);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{key} is required.") : value;
    }

    private static string String(Dictionary<string, object?> args, string key, string fallback = "") =>
        LocalIntegrationHost.String(args, key, fallback);
}
