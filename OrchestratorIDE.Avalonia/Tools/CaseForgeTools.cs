// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class CaseForgeTools
{
    private const int MaxJsonBytes = 1_000_000;
    private static readonly Regex JobIdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static void Register(ToolRegistry registry, Uri workerUrl, string? bearerToken = null,
        Uri? workspaceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!IsLocal(workerUrl))
            throw new ArgumentException("CaseForge must use a loopback, private IP, or single-label LAN host.", nameof(workerUrl));
        if (workspaceUrl is not null && !IsLocal(workspaceUrl))
            throw new ArgumentException("The CaseForge workspace must be local.", nameof(workspaceUrl));

        var http = LocalIntegrationHost.CreateClient(bearerToken, workerUrl);
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
                var id = JobId(args);
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
            await SendAsync(http, request(JobId(args), args), ct),
    };

    private static Dictionary<string, object?> BuildCreateBody(Dictionary<string, object?> args)
    {
        var mode = RequiredString(args, "mode");
        RequireOneOf(mode, "mode", "enclosure", "object", "portrait");
        var quality = RequiredString(args, "quality");
        RequireOneOf(quality, "quality", "draft", "final");
        var prompt = RequiredString(args, "prompt");
        if (prompt.Length is < 3 or > 2_000)
            throw new ArgumentException("prompt must contain 3 to 2,000 characters.");
        var style = String(args, "style", "faithful");
        RequireOneOf(style, "style", "faithful", "softened", "cartoon", "relief");
        var body = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["quality"] = quality,
            ["prompt"] = prompt,
            ["style"] = style,
            ["seed"] = Seed(args),
            ["outputs"] = new[] { "stl", "caseforge_project" },
            ["retain_inputs"] = Boolean(args, "retain_inputs"),
        };

        var inputs = String(args, "inputs_json", "[]");
        if (Encoding.UTF8.GetByteCount(inputs) > MaxJsonBytes)
            throw new ArgumentException("inputs_json exceeds 1 MB.");
        using (var parsed = JsonDocument.Parse(inputs))
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("inputs_json must be a JSON array.");
            body["inputs"] = parsed.RootElement.Clone();
        }
        var parameters = String(args, "parameters_json", "{}");
        if (Encoding.UTF8.GetByteCount(parameters) > MaxJsonBytes)
            throw new ArgumentException("parameters_json exceeds 1 MB.");
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
            if (!Boolean(args, "rights_acknowledged"))
                throw new ArgumentException("portrait mode requires rights_acknowledged to be true.");
            body["rights_attestation"] = new
            {
                version = "caseforge-rights-v1",
                accepted_at = DateTimeOffset.UtcNow,
                acknowledgement = true,
            };
        }
        return body;
    }

    private static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.Content.Headers.ContentLength > MaxJsonBytes)
                return "[ERROR] CaseForge response exceeded 1 MB.";
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
                return "[ERROR] CaseForge response exceeded 1 MB.";
            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            return response.IsSuccessStatusCode
                ? text
                : $"[ERROR] CaseForge returned HTTP {(int)response.StatusCode}: {text}";
        }
    }

    private static bool IsLocal(Uri uri) => LocalIntegrationHost.IsLocal(uri);

    private static string RequiredString(Dictionary<string, object?> args, string key)
    {
        var value = String(args, key);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{key} is required.") : value;
    }

    private static string String(Dictionary<string, object?> args, string key, string fallback = "") =>
        LocalIntegrationHost.String(args, key, fallback);

    private static bool Boolean(Dictionary<string, object?> args, string key) =>
        bool.TryParse(String(args, key), out var value) && value;

    private static long Seed(Dictionary<string, object?> args)
    {
        var text = String(args, "seed", "0");
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= uint.MaxValue
                ? value
                : throw new ArgumentException("seed must be an integer from 0 through 4294967295.");
    }

    private static void AddDimension(Dictionary<string, double> dimensions, string outputKey,
        Dictionary<string, object?> args, string inputKey)
    {
        if (!args.ContainsKey(inputKey)) return;
        if (!double.TryParse(String(args, inputKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value is <= 0 or > 2_000)
            throw new ArgumentException($"{inputKey} must be greater than 0 and no more than 2,000 mm.");
        dimensions[outputKey] = value;
    }

    private static void RequireOneOf(string value, string name, params string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{name} must be one of: {string.Join(", ", allowed)}.");
    }

    private static string JobId(Dictionary<string, object?> args)
    {
        var id = RequiredString(args, "job_id");
        return JobIdPattern.IsMatch(id) ? id : throw new ArgumentException("job_id must be a 32-character lowercase hex identifier.");
    }
}
