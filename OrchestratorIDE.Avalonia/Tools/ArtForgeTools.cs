// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class ArtForgeTools
{
    private const int MaxJsonBytes = 1_000_000;
    private static readonly Regex JobIdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex SizePattern = new("^(?<width>[0-9]{3,4})x(?<height>[0-9]{3,4})$", RegexOptions.CultureInvariant);

    public static void Register(ToolRegistry registry, Uri serviceUrl, string? bearerToken = null,
        Uri? workspaceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!IsLocal(serviceUrl))
            throw new ArgumentException("Art Forge Studio must use a loopback, private IP, or single-label LAN host.", nameof(serviceUrl));
        if (workspaceUrl is not null && !IsLocal(workspaceUrl))
            throw new ArgumentException("The Art Forge Studio workspace must be local.", nameof(workspaceUrl));

        var http = LocalIntegrationHost.CreateClient(bearerToken);
        var api = serviceUrl.ToString().TrimEnd('/');
        var editor = (workspaceUrl ?? serviceUrl).ToString().TrimEnd('/');

        registry.Register(new ToolDefinition
        {
            Name = "image_create",
            Description = "Create an image with the local Art Forge Studio and ComfyUI runtime.",
            Parameters = new()
            {
                ["prompt"] = new("string", "Image description"),
                ["negative_prompt"] = new("string", "Optional unwanted details"),
                ["checkpoint"] = new("string", "Optional installed checkpoint name"),
                ["lora"] = new("string", "Optional installed LoRA name"),
                ["size"] = new("string", "Image size such as 768x768"),
                ["seed"] = new("integer", "Deterministic generation seed"),
                ["style"] = new("string", "Optional style guidance"),
                ["content_profile"] = new("string", "sfw or mature_capable; defaults to sfw"),
            },
            Required = ["prompt"],
            RequiresApproval = true,
            Handler = async (args, ct) =>
            {
                using var content = new StringContent(JsonSerializer.Serialize(BuildCreateBody(args)), Encoding.UTF8, "application/json");
                return await SendJsonAsync(http,
                    new HttpRequestMessage(HttpMethod.Post, $"{api}/api/v1/generations/images") { Content = content }, ct);
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "image_status",
            Description = "Read a local Art Forge Studio image job's progress and outputs.",
            Parameters = new() { ["job_id"] = new("string", "Art Forge Studio job identifier") },
            Required = ["job_id"],
            Handler = async (args, ct) => await SendJsonAsync(http,
                new HttpRequestMessage(HttpMethod.Get, $"{api}/api/v1/jobs/{JobId(args)}"), ct),
        });

        registry.Register(new ToolDefinition
        {
            Name = "image_gallery",
            Description = "List recent local Art Forge Studio gallery assets.",
            Parameters = new() { ["limit"] = new("integer", "Number of assets from 1 through 100") },
            Handler = async (args, ct) => await SendJsonAsync(http,
                new HttpRequestMessage(HttpMethod.Get, $"{api}/api/v1/gallery?limit={Limit(args)}"), ct),
        });

        registry.Register(new ToolDefinition
        {
            Name = "image_open",
            Description = "Open the local Art Forge Studio workspace.",
            Parameters = new(),
            RequiresApproval = true,
            Handler = (_, _) =>
            {
                Process.Start(new ProcessStartInfo(editor) { UseShellExecute = true });
                return Task.FromResult($"Opened Art Forge Studio: {editor}");
            },
        });
    }

    private static Dictionary<string, object?> BuildCreateBody(Dictionary<string, object?> args)
    {
        var prompt = RequiredString(args, "prompt");
        if (prompt.Length > 2_000)
            throw new ArgumentException("prompt must contain no more than 2,000 characters.");
        var size = String(args, "size", "768x768");
        var match = SizePattern.Match(size);
        if (!match.Success
            || int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture) is < 256 or > 2048
            || int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture) is < 256 or > 2048)
            throw new ArgumentException("size must be WIDTHxHEIGHT with each dimension from 256 through 2048.");
        var profile = String(args, "content_profile", "sfw");
        if (profile is not ("sfw" or "mature_capable"))
            throw new ArgumentException("content_profile must be sfw or mature_capable.");

        var body = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["negative_prompt"] = String(args, "negative_prompt"),
            ["size"] = size,
            ["seed"] = Seed(args),
            ["style"] = String(args, "style"),
            ["content_profile"] = profile,
        };
        AddWhenPresent(body, args, "checkpoint");
        AddWhenPresent(body, args, "lora");
        return body;
    }

    private static async Task<string> SendJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.Content.Headers.ContentLength > MaxJsonBytes)
                return "[ERROR] Art Forge Studio response exceeded 1 MB.";
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
                return "[ERROR] Art Forge Studio response exceeded 1 MB.";
            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            return response.IsSuccessStatusCode
                ? text
                : $"[ERROR] Art Forge Studio returned HTTP {(int)response.StatusCode}: {text}";
        }
    }

    private static bool IsLocal(Uri uri) => LocalIntegrationHost.IsLocal(uri);

    private static string JobId(Dictionary<string, object?> args)
    {
        var id = RequiredString(args, "job_id");
        return JobIdPattern.IsMatch(id) ? id : throw new ArgumentException("job_id must be a 32-character lowercase hex identifier.");
    }

    private static int Limit(Dictionary<string, object?> args)
    {
        var value = String(args, "limit", "30");
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            && limit is >= 1 and <= 100
                ? limit
                : throw new ArgumentException("limit must be an integer from 1 through 100.");
    }

    private static long Seed(Dictionary<string, object?> args)
    {
        var text = String(args, "seed", "0");
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= uint.MaxValue
                ? value
                : throw new ArgumentException("seed must be an integer from 0 through 4294967295.");
    }

    private static void AddWhenPresent(Dictionary<string, object?> body, Dictionary<string, object?> args, string key)
    {
        var value = String(args, key);
        if (!string.IsNullOrWhiteSpace(value)) body[key] = value;
    }

    private static string RequiredString(Dictionary<string, object?> args, string key)
    {
        var value = String(args, key);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{key} is required.") : value;
    }

    private static string String(Dictionary<string, object?> args, string key, string fallback = "") =>
        LocalIntegrationHost.String(args, key, fallback);
}
