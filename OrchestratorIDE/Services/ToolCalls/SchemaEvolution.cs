// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Evolution configuration ───────────────────────────────────────────────────

/// <summary>
/// Controls how SchemaEvolution runs.
/// </summary>
public record EvolutionConfig(
    int    Generations   = 3,     // how many mutation rounds to run
    int    VariantsPerGen = 4,    // how many variants to test per generation
    bool   PromoteWinners = true, // auto-promote winners to SchemaLibrary after run
    int    TimeoutSeconds = 30    // per-probe timeout
);

/// <summary>Progress event fired during an evolution run.</summary>
public record EvolutionProgress(
    string ToolName,
    int    Generation,
    int    VariantIndex,
    int    TotalVariants,
    string MutationType,
    bool?  Passed,
    string Message
);

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 5 — Evolutionary Schema Search.
///
/// Systematically mutates tool schemas to discover the highest-fitness calling
/// convention for a specific model.  Runs as an overnight / on-demand task.
///
/// Algorithm:
///   1. Start from the seed schema (current default for the tool)
///   2. Generate N variants by applying different mutations
///   3. Send each variant to the model with a matching probe prompt
///   4. Measure: did the model call the tool correctly?
///   5. Select the fittest survivors and breed the next generation
///   6. Promote winners to SchemaLibrary and FitnessMap
///
/// This is the most expensive phase (~N×G API calls per tool) so it runs
/// on-demand, not automatically on first probe.
/// </summary>
public class SchemaEvolution
{
    private readonly string     _ollamaHost;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented        = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SchemaEvolution(string ollamaHost = "http://localhost:11434")
    {
        _ollamaHost = ollamaHost.TrimEnd('/');
        _http       = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Run the full evolutionary search for all tools in <paramref name="tools"/>
    /// against the given model.  Reports progress via <paramref name="onProgress"/>.
    /// Returns a summary of winners per tool.
    /// </summary>
    public async Task<Dictionary<string, SchemaVariant?>> RunAsync(
        string                          model,
        IReadOnlyList<ToolDefinition>   tools,
        EvolutionConfig?                config      = null,
        Action<EvolutionProgress>?      onProgress  = null,
        CancellationToken               ct          = default)
    {
        config ??= new EvolutionConfig();
        var winners = new Dictionary<string, SchemaVariant?>();

        foreach (var tool in tools)
        {
            if (ct.IsCancellationRequested) break;

            var winner = await EvolveToolAsync(model, tool, config, onProgress, ct);
            winners[tool.Name] = winner;

            if (winner != null && config.PromoteWinners)
            {
                var format = ToolCallProfileStore.GetPreferredFormat(model);
                await SchemaLibrary.SaveConfirmedAsync(
                    modelId:       model,
                    toolName:      tool.Name,
                    format:        format,
                    schemaPayload: winner.SchemaPayload,
                    successCount:  3);
            }
        }

        return winners;
    }

    // ── Single-tool evolution ─────────────────────────────────────────────────

    private async Task<SchemaVariant?> EvolveToolAsync(
        string                     model,
        ToolDefinition             tool,
        EvolutionConfig            config,
        Action<EvolutionProgress>? onProgress,
        CancellationToken          ct)
    {
        // Seed: the current default schema (already simplified if rules apply)
        var seedPayload = tool.ToOllamaSchema();
        var seed = new SchemaVariant(
            VariantId:     $"gen0_seed",
            ToolName:      tool.Name,
            Mutation:      MutationType.Original,
            SchemaPayload: seedPayload,
            Passed:        null,
            ParsedOutput:  null,
            FailReason:    null,
            TestedAt:      DateTime.UtcNow);

        // Test the seed first
        seed = await TestVariantAsync(model, tool, seed, onProgress, ct);
        await FitnessMap.RecordVariantAsync(model, tool.Name, seed);

        // If the seed already passes, we may not need evolution — but run anyway
        // to find an even better schema.
        var currentGen = new List<SchemaVariant> { seed };
        SchemaVariant? bestWinner = seed.IsWinner ? seed : null;

        for (int gen = 1; gen <= config.Generations; gen++)
        {
            if (ct.IsCancellationRequested) break;

            var mutations = GenerateMutations(currentGen, gen, config.VariantsPerGen, tool);
            var genWinners = new List<SchemaVariant>();

            foreach (var variant in mutations)
            {
                if (ct.IsCancellationRequested) break;
                var tested = await TestVariantAsync(model, tool, variant, onProgress, ct);
                await FitnessMap.RecordVariantAsync(model, tool.Name, tested);
                if (tested.IsWinner)
                {
                    genWinners.Add(tested);
                    bestWinner ??= tested;
                }
            }

            // Next generation seeds: winners from this gen, or fallback to full pool
            currentGen = genWinners.Count > 0 ? genWinners : [seed];
        }

        return bestWinner;
    }

    // ── Mutation generation ───────────────────────────────────────────────────

    private static List<SchemaVariant> GenerateMutations(
        IReadOnlyList<SchemaVariant> parents,
        int generation,
        int count,
        ToolDefinition tool)
    {
        var mutations = new List<SchemaVariant>();
        var seedPayload = JsonNode.Parse(JsonSerializer.Serialize(tool.ToOllamaSchema()))!;

        var allMutations = new[]
        {
            MutationType.ShortenDescription,
            MutationType.VerboseDescription,
            MutationType.RemoveOptional,
            MutationType.StripEnums,
            MutationType.AddType,
            MutationType.RemoveType,
            MutationType.RenameField,
            MutationType.FlattenFields,
        };

        // Cycle through mutation types, starting from where we left off
        for (int i = 0; i < Math.Min(count, allMutations.Length); i++)
        {
            var mutType = allMutations[(generation + i) % allMutations.Length];
            var basePayload = parents[i % parents.Count].SchemaPayload;
            var mutated = ApplyMutation(basePayload, mutType, tool);
            if (mutated == null) continue;

            mutations.Add(new SchemaVariant(
                VariantId:     $"gen{generation}_v{i}",
                ToolName:      tool.Name,
                Mutation:      mutType,
                SchemaPayload: mutated,
                Passed:        null,
                ParsedOutput:  null,
                FailReason:    null,
                TestedAt:      DateTime.UtcNow));
        }

        return mutations;
    }

    private static object? ApplyMutation(object basePayload, MutationType mutation, ToolDefinition tool)
    {
        try
        {
            var json = JsonSerializer.Serialize(basePayload);
            var node = JsonNode.Parse(json);
            if (node == null) return null;

            var paramNode = node["function"]?["parameters"];
            if (paramNode == null) return basePayload;

            switch (mutation)
            {
                case MutationType.ShortenDescription:
                {
                    var fnDesc = node["function"]?["description"];
                    if (fnDesc != null)
                    {
                        var words = fnDesc.GetValue<string>().Split(' ');
                        node["function"]!["description"] =
                            string.Join(' ', words.Take(Math.Min(5, words.Length)));
                    }
                    ShortenAllFieldDescriptions(paramNode, maxWords: 5);
                    break;
                }

                case MutationType.VerboseDescription:
                {
                    var fnDesc = node["function"]?["description"]?.GetValue<string>() ?? "";
                    node["function"]!["description"] =
                        $"{fnDesc} Call this function when the task requires {tool.Name.Replace('_', ' ')}.";
                    break;
                }

                case MutationType.RemoveOptional:
                {
                    var props    = paramNode["properties"]?.AsObject();
                    var required = paramNode["required"]?.AsArray();
                    if (props != null && required != null)
                    {
                        var reqSet = required.Select(r => r?.GetValue<string>())
                            .Where(r => r != null).ToHashSet()!;
                        var toRemove = props.Select(p => p.Key)
                            .Where(k => !reqSet.Contains(k)).ToList();
                        foreach (var key in toRemove) props.Remove(key);
                    }
                    break;
                }

                case MutationType.StripEnums:
                {
                    var props = paramNode["properties"]?.AsObject();
                    if (props != null)
                        foreach (var prop in props)
                            if (prop.Value is JsonObject obj && obj.ContainsKey("enum"))
                            {
                                obj.Remove("enum");
                                obj["type"] = "string";
                            }
                    break;
                }

                case MutationType.AddType:
                {
                    var props = paramNode["properties"]?.AsObject();
                    if (props != null)
                        foreach (var prop in props)
                            if (prop.Value is JsonObject obj && !obj.ContainsKey("type"))
                                obj["type"] = "string";
                    break;
                }

                case MutationType.RemoveType:
                {
                    var props = paramNode["properties"]?.AsObject();
                    if (props != null)
                        foreach (var prop in props)
                            if (prop.Value is JsonObject obj)
                                obj.Remove("type");
                    break;
                }

                case MutationType.RenameField:
                {
                    // Rename first non-required field to snake_case if it uses camelCase
                    var props    = paramNode["properties"]?.AsObject();
                    var required = paramNode["required"]?.AsArray();
                    if (props != null)
                    {
                        var reqSet = required?.Select(r => r?.GetValue<string>())
                            .Where(r => r != null).ToHashSet() ?? [];
                        var candidate = props
                            .FirstOrDefault(p => reqSet.Contains(p.Key) &&
                                            p.Key.Any(char.IsUpper));
                        if (candidate.Key != null)
                        {
                            var snake = ToSnakeCase(candidate.Key);
                            if (snake != candidate.Key)
                            {
                                var val = candidate.Value?.DeepClone();
                                props.Remove(candidate.Key);
                                props[snake] = val;
                                // Update required array
                                if (required != null)
                                    for (int i = 0; i < required.Count; i++)
                                        if (required[i]?.GetValue<string>() == candidate.Key)
                                        {
                                            required[i] = JsonValue.Create(snake);
                                            break;
                                        }
                            }
                        }
                    }
                    break;
                }

                case MutationType.FlattenFields:
                {
                    // Apply existing SchemaSimplifier flatten logic via its rules
                    var rules = new SchemaSimplificationRules(FlattenNested: true);
                    var list  = new List<object> { node! };
                    var simplified = SchemaSimplifier.Apply(list, rules);
                    return simplified.Count > 0 ? simplified[0] : basePayload;
                }

                default:
                    return basePayload;
            }

            return node;
        }
        catch { return basePayload; }
    }

    // ── Probe a single variant ────────────────────────────────────────────────

    private async Task<SchemaVariant> TestVariantAsync(
        string                     model,
        ToolDefinition             tool,
        SchemaVariant              variant,
        Action<EvolutionProgress>? onProgress,
        CancellationToken          ct)
    {
        // Build a minimal probe: single tool, single message that naturally calls it
        var prompt = BuildProbePrompt(tool);

        onProgress?.Invoke(new EvolutionProgress(
            ToolName:      tool.Name,
            Generation:    ExtractGen(variant.VariantId),
            VariantIndex:  ExtractVariantIdx(variant.VariantId),
            TotalVariants: -1,
            MutationType:  variant.Mutation.ToString(),
            Passed:        null,
            Message:       $"Testing {variant.Mutation}…"));

        try
        {
            var payload = new
            {
                model    = model,
                messages = new[] { new { role = "user", content = prompt } },
                tools    = new[] { variant.SchemaPayload },
                stream   = false,
            };

            var body    = new StringContent(JsonSerializer.Serialize(payload, _json),
                              Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var resp = await _http.PostAsync(
                $"{_ollamaHost}/v1/chat/completions", body, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                var failed = variant with
                {
                    Passed     = false,
                    FailReason = $"HTTP {(int)resp.StatusCode}: {err[..Math.Min(120, err.Length)]}",
                    TestedAt   = DateTime.UtcNow,
                };
                onProgress?.Invoke(new EvolutionProgress(
                    ToolName: tool.Name, Generation: ExtractGen(variant.VariantId),
                    VariantIndex: 0, TotalVariants: -1,
                    MutationType: variant.Mutation.ToString(),
                    Passed: false, Message: failed.FailReason!));
                return failed;
            }

            var respText  = await resp.Content.ReadAsStringAsync(ct);
            var (passed, output, failReason) = ParseProbeResponse(respText, tool.Name);

            var result = variant with
            {
                Passed       = passed,
                ParsedOutput = output,
                FailReason   = failReason,
                TestedAt     = DateTime.UtcNow,
            };

            onProgress?.Invoke(new EvolutionProgress(
                ToolName: tool.Name, Generation: ExtractGen(variant.VariantId),
                VariantIndex: 0, TotalVariants: -1,
                MutationType: variant.Mutation.ToString(),
                Passed: passed, Message: passed ? "✓ PASS" : $"✗ {failReason}"));

            return result;
        }
        catch (OperationCanceledException)
        {
            return variant with { Passed = false, FailReason = "Timeout", TestedAt = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            return variant with { Passed = false, FailReason = ex.Message[..Math.Min(120, ex.Message.Length)], TestedAt = DateTime.UtcNow };
        }
    }

    // ── Probe prompt ──────────────────────────────────────────────────────────

    private static string BuildProbePrompt(ToolDefinition tool)
    {
        // Build a minimal natural-language prompt that should cause the model
        // to call this specific tool with at least the required arguments.
        var sb = new StringBuilder();
        sb.Append($"Please call the '{tool.Name}' function");

        if (tool.Parameters.Count > 0)
        {
            var required = (IReadOnlyCollection<string>)tool.Required;
            var sample = tool.Parameters
                .Where(kv => required.Contains(kv.Key))
                .Select(kv => BuildSampleValue(kv.Key, kv.Value))
                .ToList();

            if (sample.Count > 0)
                sb.Append($" with {string.Join(", ", sample)}");
        }

        sb.Append(". Only call the function — do not explain or add text.");
        return sb.ToString();
    }

    private static string BuildSampleValue(string name, ToolParameter param)
    {
        var val = (param.Type ?? "string") switch
        {
            "number"  or "integer" => "42",
            "boolean"              => "true",
            "array"                => "[\"item\"]",
            _                     => $"\"{name}_value\"",
        };
        return $"{name}={val}";
    }

    // ── Response parsing ──────────────────────────────────────────────────────

    private static (bool passed, string? output, string? failReason)
        ParseProbeResponse(string respText, string expectedToolName)
    {
        try
        {
            var doc  = JsonDocument.Parse(respText);
            var root = doc.RootElement;

            // Check choices[0].message.tool_calls
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return (false, null, "No choices in response");

            var msg = choices[0].GetProperty("message");

            if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                var first = toolCalls[0];
                var name  = first.TryGetProperty("function", out var fn)
                    && fn.TryGetProperty("name", out var n)
                    ? n.GetString() : null;
                var args  = fn.TryGetProperty("arguments", out var a) ? a.ToString() : null;

                if (string.Equals(name, expectedToolName, StringComparison.OrdinalIgnoreCase))
                    return (true, args, null);

                return (false, null, $"Wrong tool called: '{name}' (expected '{expectedToolName}')");
            }

            // Fallback: check if model produced JSON text with the tool name
            if (msg.TryGetProperty("content", out var contentEl))
            {
                var content = contentEl.GetString() ?? "";
                if (content.Contains($"\"name\": \"{expectedToolName}\"", StringComparison.OrdinalIgnoreCase)
                    || content.Contains($"\"tool\": \"{expectedToolName}\"", StringComparison.OrdinalIgnoreCase))
                    return (true, content[..Math.Min(200, content.Length)], null);

                return (false, content[..Math.Min(200, content.Length)],
                    "Model produced text instead of tool call");
            }

            return (false, null, "Empty response");
        }
        catch (Exception ex)
        {
            return (false, null, $"Parse error: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static void ShortenAllFieldDescriptions(JsonNode paramNode, int maxWords)
    {
        var props = paramNode["properties"]?.AsObject();
        if (props == null) return;
        foreach (var prop in props)
            if (prop.Value is JsonObject obj && obj["description"] is JsonValue desc)
            {
                var words = desc.GetValue<string>().Split(' ');
                if (words.Length > maxWords)
                    obj["description"] = JsonValue.Create(
                        string.Join(' ', words.Take(maxWords)) + ".");
            }
    }

    private static string ToSnakeCase(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsUpper(s[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLower(s[i]));
        }
        return sb.ToString();
    }

    private static int ExtractGen(string variantId)
    {
        var parts = variantId.Split('_');
        return parts.Length > 0 && parts[0].StartsWith("gen")
            && int.TryParse(parts[0][3..], out var g) ? g : 0;
    }

    private static int ExtractVariantIdx(string variantId)
    {
        var parts = variantId.Split('_');
        return parts.Length > 1 && parts[1].StartsWith("v")
            && int.TryParse(parts[1][1..], out var v) ? v : 0;
    }
}
