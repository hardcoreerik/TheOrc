// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class CaseForgeTools
{
    public static void Register(ToolRegistry registry, Uri workerUrl, string? bearerToken = null,
        Uri? workspaceUrl = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!IsLocal(workerUrl))
            throw new ArgumentException("CaseForge must use a loopback, private IP, or single-label LAN host.", nameof(workerUrl));

        var http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        var api = workerUrl.ToString().TrimEnd('/');
        var editor = (workspaceUrl ?? workerUrl).ToString().TrimEnd('/');

        registry.Register(new ToolDefinition
        {
            Name = "model3d_create",
            Description = "Create a local CaseForge enclosure, object, or portrait job. Final GPU work requires approval.",
            Parameters = new()
            {
                ["mode"] = new("string", "enclosure, object, or portrait"),
                ["quality"] = new("string", "draft or final"),
                ["prompt"] = new("string", "What to create or modify"),
                ["inputs_json"] = new("string", "Optional JSON array of content-addressed input artifacts"),
                ["parameters_json"] = new("string", "Optional JSON object of validated enclosure parameters"),
                ["width_mm"] = new("number", "Optional target width in millimetres"),
                ["depth_mm"] = new("number", "Optional target depth in millimetres"),
                ["height_mm"] = new("number", "Optional target height in millimetres"),
                ["style"] = new("string", "faithful, softened, cartoon, or relief"),
                ["seed"] = new("integer", "Deterministic generation seed"),
                ["retain_inputs"] = new("boolean", "Keep source images for later revisions"),
                ["rights_acknowledged"] = new("boolean", "Confirms rights or permission for portrait photos"),
            },
            Required = ["mode", "quality", "prompt"],
            RequiresApproval = true,
            Handler = async (args, ct) =>
            {
                var body = BuildCreateBody(args);
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                return await SendAsync(http, new(HttpMethod.Post, $"{api}/v1/jobs") { Content = content }, ct);
            },
        });

        registry.Register(JobTool("model3d_status", "Read a local CaseForge job's progress and audit report.",
            false, (id, _) => new(HttpMethod.Get, $"{api}/v1/jobs/{Uri.EscapeDataString(id)}"), http));
        registry.Register(JobTool("model3d_cancel", "Cancel a queued or running local CaseForge job.",
            true, (id, _) => new(HttpMethod.Post, $"{api}/v1/jobs/{Uri.EscapeDataString(id)}/cancel"), http));
        registry.Register(new ToolDefinition
        {
            Name = "model3d_open",
            Description = "Open a CaseForge job in the local 3D workspace.",
            Parameters = new() { ["job_id"] = new("string", "CaseForge job identifier") },
            Required = ["job_id"],
            RequiresApproval = true,
            Handler = (args, _) =>
            {
                var id = RequiredString(args, "job_id");
                var url = $"{editor}/?job={Uri.EscapeDataString(id)}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Task.FromResult($"Opened CaseForge job {id}: {url}");
            },
        });
    }

    private static ToolDefinition JobTool(string name, string description, bool approval,
        Func<string, Dictionary<string, object?>, HttpRequestMessage> request,
        HttpClient http) => new()
    {
        Name = name,
        Description = description,
        Parameters = new() { ["job_id"] = new("string", "CaseForge job identifier") },
        Required = ["job_id"],
        RequiresApproval = approval,
        Handler = async (args, ct) =>
            await SendAsync(http, request(RequiredString(args, "job_id"), args), ct),
    };

    private static Dictionary<string, object?> BuildCreateBody(Dictionary<string, object?> args)
    {
        var mode = RequiredString(args, "mode");
        var body = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["quality"] = RequiredString(args, "quality"),
            ["prompt"] = RequiredString(args, "prompt"),
            ["style"] = String(args, "style", "faithful"),
            ["seed"] = Number(args, "seed", 0),
            ["outputs"] = new[] { "stl", "caseforge_project" },
            ["retain_inputs"] = Boolean(args, "retain_inputs"),
        };

        var inputs = String(args, "inputs_json", "[]");
        using (var parsed = JsonDocument.Parse(inputs))
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("inputs_json must be a JSON array.");
            body["inputs"] = parsed.RootElement.Clone();
        }
        var parameters = String(args, "parameters_json", "{}");
        using (var parsed = JsonDocument.Parse(parameters))
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("parameters_json must be a JSON object.");
            body["parameters"] = parsed.RootElement.Clone();
        }

        var dimensions = new Dictionary<string, double>();
        AddDimension(dimensions, "width", args, "width_mm");
        AddDimension(dimensions, "depth", args, "depth_mm");
        AddDimension(dimensions, "height", args, "height_mm");
        if (dimensions.Count > 0) body["dimensions_mm"] = dimensions;

        if (mode.Equals("portrait", StringComparison.OrdinalIgnoreCase))
        {
            body["rights_attestation"] = new
            {
                version = "caseforge-rights-v1",
                accepted_at = DateTimeOffset.UtcNow,
                acknowledgement = Boolean(args, "rights_acknowledged"),
            };
        }
        return body;
    }

    private static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > 1_000_000) return "[ERROR] CaseForge response exceeded 1 MB.";
            return response.IsSuccessStatusCode
                ? text
                : $"[ERROR] CaseForge returned HTTP {(int)response.StatusCode}: {text}";
        }
    }

    private static bool IsLocal(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (uri.IsLoopback || !uri.Host.Contains('.')) return true;
        if (!IPAddress.TryParse(uri.Host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    private static string RequiredString(Dictionary<string, object?> args, string key)
    {
        var value = String(args, key);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{key} is required.") : value;
    }

    private static string String(Dictionary<string, object?> args, string key, string fallback = "") =>
        args.TryGetValue(key, out var value) ? value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            _ => value?.ToString() ?? fallback,
        } : fallback;

    private static bool Boolean(Dictionary<string, object?> args, string key) =>
        bool.TryParse(String(args, key), out var value) && value;

    private static long Number(Dictionary<string, object?> args, string key, long fallback) =>
        long.TryParse(String(args, key), out var value) ? value : fallback;

    private static void AddDimension(Dictionary<string, double> dimensions, string outputKey,
        Dictionary<string, object?> args, string inputKey)
    {
        if (double.TryParse(String(args, inputKey), out var value) && value > 0)
            dimensions[outputKey] = value;
    }
}
