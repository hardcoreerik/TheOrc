using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>
/// A tool schema ready to send to the Ollama API — shaped for a specific model's
/// format preference and complexity threshold.
/// </summary>
public record GeneratedToolSchema(
    string        ToolName,
    string        ModelId,
    FormatVariant Format,
    object        Payload      // serializable Ollama-API schema object
)
{
    public bool IsEmpty => Payload is null;
}

/// <summary>
/// A small text example of a model producing a correct tool call.
/// Collected from successful probe runs and stored in ToolCallProfile.FewShotExamples.
/// </summary>
public record FewShotExample(
    string ToolName,
    string InputPrompt,
    string ModelOutput,   // the raw model response text that parsed correctly
    FormatVariant Format,
    DateTime CapturedAt
);

// ── Generator ─────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 3 — Adaptive Schema Generation.
///
/// Combines the model's FormatFingerprint + CategoryBoundaryMap to generate
/// tool schemas shaped to that model's capabilities.
///
/// Two modes:
///   GenerateForRole()          — returns the role-appropriate tool set for a model
///   BootstrapFromExamples()    — infers schema structure from a few-shot example set
/// </summary>
public static class SchemaGenerator
{
    // ── Role-set generation ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a list of tool schema payloads for the given role, shaped for
    /// the model's confirmed format and complexity level.
    ///
    /// If the model has confirmed schemas in SchemaLibrary for any tools in the
    /// role set, those are used instead of the generated ones.
    /// </summary>
    public static IReadOnlyList<object> GenerateForRole(
        IReadOnlyList<ToolDefinition> allTools,
        string modelId)
    {
        var profile = ToolCallProfileStore.Load(modelId);
        var format  = profile?.FormatProfile?.PreferredFormat ?? FormatVariant.BareJson;
        var rules   = profile?.Simplification ?? new SchemaSimplificationRules();

        var result = new List<object>();

        foreach (var tool in allTools)
        {
            // Prefer confirmed schema from library if available and reliable
            var confirmed = SchemaLibrary.GetBestSchema(modelId, tool.Name);
            if (confirmed?.IsReliable == true)
            {
                result.Add(confirmed.SchemaPayload);
                continue;
            }

            // Generate schema shaped to this model's capabilities
            var schema = BuildSchema(tool, format, rules);
            result.Add(schema);
        }

        return result;
    }

    /// <summary>
    /// Generates a single tool schema object in the model's preferred format,
    /// with simplification rules applied.
    /// </summary>
    public static object GenerateSingle(
        ToolDefinition tool,
        string         modelId)
    {
        var profile = ToolCallProfileStore.Load(modelId);
        var format  = profile?.FormatProfile?.PreferredFormat ?? FormatVariant.BareJson;
        var rules   = profile?.Simplification ?? new SchemaSimplificationRules();
        return BuildSchema(tool, format, rules);
    }

    // ── Few-shot bootstrapping ────────────────────────────────────────────────

    /// <summary>
    /// Infers a tool schema from a set of few-shot examples where the model
    /// successfully produced a tool call.
    ///
    /// Strategy: parse each example's output to extract field names and values,
    /// then build a minimal schema that covers all observed fields.
    ///
    /// This is "discovery from success" — no fabrication, only observed fields.
    /// </summary>
    public static object? BootstrapFromExamples(
        IReadOnlyList<FewShotExample> examples,
        string                        toolDescription,
        string                        modelId)
    {
        if (examples.Count == 0) return null;

        // Collect all field names seen across examples
        var observedFields = new Dictionary<string, JsonValueKind>(StringComparer.OrdinalIgnoreCase);

        foreach (var ex in examples)
        {
            var fields = ExtractFieldsFromOutput(ex.ModelOutput, ex.Format);
            foreach (var (name, kind) in fields)
            {
                if (!observedFields.ContainsKey(name))
                    observedFields[name] = kind;
            }
        }

        if (observedFields.Count == 0) return null;

        // Build a minimal schema with observed fields
        var properties = new JsonObject();
        var required   = new JsonArray();

        foreach (var (name, kind) in observedFields)
        {
            var typeStr = kind switch
            {
                JsonValueKind.Number  => "number",
                JsonValueKind.True    => "boolean",
                JsonValueKind.False   => "boolean",
                JsonValueKind.Array   => "array",
                JsonValueKind.Object  => "object",
                _                    => "string",
            };
            properties[name] = new JsonObject
            {
                ["type"]        = typeStr,
                ["description"] = name,   // minimal description — field name only
            };
            required.Add(name);
        }

        var profile = ToolCallProfileStore.Load(modelId);
        var toolName = examples[0].ToolName;

        return new
        {
            type = "function",
            function = new
            {
                name        = toolName,
                description = toolDescription,
                parameters  = new
                {
                    type       = "object",
                    properties = properties,
                    required   = required,
                }
            }
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object BuildSchema(
        ToolDefinition            tool,
        FormatVariant             format,
        SchemaSimplificationRules rules)
    {
        // Start from the Ollama schema shape
        var raw    = JsonSerializer.Serialize(tool.ToOllamaSchema());
        var node   = JsonNode.Parse(raw);
        if (node == null) return tool.ToOllamaSchema();

        // Apply simplification rules if needed
        if (!rules.IsNoOp)
        {
            var paramNode = node["function"]?["parameters"];
            if (paramNode != null)
                ApplyRulesToNode(paramNode, rules);
        }

        return node;
    }

    private static void ApplyRulesToNode(JsonNode paramNode, SchemaSimplificationRules rules)
    {
        // Delegate to SchemaSimplifier's per-node logic via Apply() on a single-item list
        var wrapped  = new JsonArray { paramNode.DeepClone() };
        var dummyTool = new JsonObject
        {
            ["function"] = new JsonObject { ["parameters"] = wrapped[0]!.DeepClone() }
        };
        // Re-use the simplifier's full pipeline on a synthesized single-tool array
        var singleTool = new JsonArray { dummyTool };
        var serialized = singleTool.ToJsonString();
        try
        {
            var list    = new List<object> { JsonNode.Parse(serialized)! };
            var applied = SchemaSimplifier.Apply(list, rules);
            if (applied.Count > 0)
            {
                // Patch the parameters node in-place from the simplified result
                var simplified = applied[0] as JsonNode
                    ?? JsonNode.Parse(JsonSerializer.Serialize(applied[0]));
                var simplParams = simplified?["function"]?["parameters"];
                if (simplParams != null)
                {
                    var parent = paramNode.Parent as JsonObject;
                    parent?.Remove("parameters");
                    parent?.Add("parameters", simplParams.DeepClone());
                }
            }
        }
        catch { /* non-fatal — use original node */ }
    }

    /// <summary>
    /// Extracts field names and their JSON value kinds from a model's raw output.
    /// Supports BareJson, OpenAiJson, and partial HermesXml.
    /// </summary>
    private static Dictionary<string, JsonValueKind> ExtractFieldsFromOutput(
        string output, FormatVariant format)
    {
        var result = new Dictionary<string, JsonValueKind>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? jsonFragment = null;

            if (format is FormatVariant.BareJson or FormatVariant.OpenAiJson)
            {
                // Find the first JSON object in the output
                var start = output.IndexOf('{');
                var end   = output.LastIndexOf('}');
                if (start >= 0 && end > start)
                    jsonFragment = output[start..(end + 1)];
            }
            else if (format == FormatVariant.HermesXml)
            {
                // Extract <parameters>...</parameters> block
                var pStart = output.IndexOf("<parameters>", StringComparison.OrdinalIgnoreCase);
                var pEnd   = output.IndexOf("</parameters>", StringComparison.OrdinalIgnoreCase);
                if (pStart >= 0 && pEnd > pStart)
                {
                    var inner = output[(pStart + 12)..pEnd];
                    jsonFragment = $"{{{inner}}}";
                }
            }

            if (jsonFragment == null) return result;

            var doc = JsonDocument.Parse(jsonFragment);

            // Look for "arguments" or "args" wrapper (OpenAI envelope) first
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("arguments", out var argsEl))
                root = argsEl;
            else if (root.TryGetProperty("args", out var argsEl2))
                root = argsEl2;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                    result[prop.Name] = prop.Value.ValueKind;
            }
        }
        catch { /* non-fatal */ }

        return result;
    }
}
