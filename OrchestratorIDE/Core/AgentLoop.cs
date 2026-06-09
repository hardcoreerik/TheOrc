using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.Core;

/// <summary>
/// The core agent loop. Supports Plan and Execute modes.
///
/// Plan mode:   model is asked to produce a structured plan only (no tool calls)
/// Execute mode: model runs with tools, approval gate, step limit, auto-verify
///
/// Emits activity events for the UI to consume in real time.
/// </summary>
public class AgentLoop
{
    private readonly OllamaClient _ollama;
    private readonly ToolRegistry _registry;
    private readonly ContextManager _context;
    private readonly Trust.GitCheckpoint _git;
    private readonly Trust.RulesLoader _rules;

    // Activity event — UI subscribes to stream the activity log
    public event Action<ActivityEvent>? Activity;

    // Token event — fires for every streamed token so the UI bubble updates live
    public event Action<string>? OnToken;

    // Usage event — fires after each model response with (promptTokens, completionTokens)
    public event Action<int, int>? OnUsage;

    // Rules event — fires when .agent.md (or other rules file) is loaded.
    // null = no rules file found; string = full path of the loaded file.
    public event Action<string?>? OnRulesLoaded;

    public AgentLoop(
        OllamaClient ollama,
        ToolRegistry registry,
        ContextManager context,
        Trust.GitCheckpoint git,
        Trust.RulesLoader rules)
    {
        _ollama = ollama;
        _registry = registry;
        _context = context;
        _git = git;
        _rules = rules;
    }

    // ── Rules refresh (call after dropping a new .agent.md) ─────────────────

    /// <summary>
    /// Re-reads the rules file for <paramref name="workspaceRoot"/> and fires
    /// <see cref="OnRulesLoaded"/> so the badge and system prompt stay in sync.
    /// Call this after programmatically writing a new .agent.md into the workspace.
    /// </summary>
    public async Task RefreshRulesAsync(string workspaceRoot)
    {
        var rulesText = await _rules.LoadAsync(workspaceRoot);
        var path      = _rules.FindRulesFile(workspaceRoot);
        OnRulesLoaded?.Invoke(string.IsNullOrEmpty(rulesText) ? null : path);
    }

    // ── Plan mode ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ask the model to produce a plan without executing anything.
    /// Returns the plan text for user review.
    /// </summary>
    public async Task<string> PlanAsync(
        ProjectSession session,
        string userPrompt,
        CancellationToken ct)
    {
        Emit(ActivityKind.Info, "Planning…", "Reading task and building plan.");

        var profile = ModelProfiles.Get(session.ActiveModel);

        // Load project rules so the plan knows the project conventions
        var rulesText = await _rules.LoadAsync(session.WorkspaceRoot);
        if (!string.IsNullOrEmpty(rulesText))
        {
            Emit(ActivityKind.Info, "Rules loaded", $"{rulesText.Length} chars injected into plan prompt");
            OnRulesLoaded?.Invoke(_rules.FindRulesFile(session.WorkspaceRoot));
        }
        else
        {
            OnRulesLoaded?.Invoke(null);
        }

        var messages = BuildMessages(session, userPrompt, planOnly: true, rulesText: rulesText);

        var planText = new System.Text.StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            session.ActiveModel, messages,
            tools: null,   // no tools in plan mode
            temperature: profile.Temperature,
            onUsage: (p, c) => OnUsage?.Invoke(p, c),
            ct: ct))
        {
            planText.Append(token);
            OnToken?.Invoke(token);  // Live streaming to UI bubble
        }

        var plan = planText.ToString().Trim();
        session.PlanText = plan;

        // Strip fake tool-call JSON blocks the model may have written in the plan
        // (e.g. ```json {"name":"create_project",...}```).
        // These don't correspond to real tools and confuse the Execute phase.
        var cleanPlan = StripFakeToolBlocks(plan);

        // Add to session messages so Execute sees the plan in its history
        session.Messages.Add(new AgentMessage { Role = MessageRole.User,      Content = userPrompt });
        session.Messages.Add(new AgentMessage { Role = MessageRole.Assistant,  Content = cleanPlan });

        Emit(ActivityKind.Info, "Plan ready", "Review the plan and click Execute when ready.");
        return plan;
    }

    // ── Execute mode ─────────────────────────────────────────────────────────

    /// <summary>
    /// Execute the current plan (or a direct task) with full tool access.
    /// Streams activity events throughout. Returns final response text.
    /// </summary>
    public async Task<string> ExecuteAsync(
        ProjectSession session,
        string userPrompt,
        CancellationToken ct)
    {
        var profile = ModelProfiles.Get(session.ActiveModel);
        var tools   = _registry.GetForProfile(profile);

        // ── Tool-call dispatch: check probe profile ─────────────────────────
        // If the probe tester has run and found that native API doesn't work for
        // this model but text-JSON does, skip the tools array entirely so the
        // model falls through to the text-format parser path automatically.
        var tcMode = ToolCallProfileStore.GetMode(session.ActiveModel,
            profileDefault: profile.NativeToolUse);  // fall back to static profile flag
        var useNativeTools = tcMode is ToolCallMode.Native or ToolCallMode.Both or ToolCallMode.Unknown;
        if (!useNativeTools)
        {
            Emit(ActivityKind.Info, "Tool dispatch",
                $"Profile: {tcMode} — skipping tools[] array, relying on text-JSON format.");
            tools = [];  // force text-JSON path — system prompt has the format instructions
        }
        else if (tcMode != ToolCallMode.Unknown)
        {
            Emit(ActivityKind.Debug, "Tool dispatch",
                $"Profile: {tcMode} — sending tools[] array.", 3);
        }

        // ── GOBLIN MIND: Schema Library + Simplification ────────────────────
        // SchemaGenerator.GenerateForRole() handles the full pipeline:
        //   1. Check SchemaLibrary for confirmed schemas per tool
        //   2. Apply SchemaSimplificationRules for models that fail on complexity
        //   3. Return serializable schema objects ready for OllamaClient
        // Users never see any of this — it's transparent middleware.
        var simplRules = ToolCallProfileStore.GetSimplificationRules(session.ActiveModel);
        IReadOnlyList<object> toolsPayload;
        if (tools.Count == 0)
        {
            toolsPayload = [];
        }
        else
        {
            toolsPayload = SchemaGenerator.GenerateForRole(tools, session.ActiveModel);

            if (simplRules != null && !simplRules.IsNoOp)
                Emit(ActivityKind.Debug, "Schema simplifier",
                    $"Applied rules: {simplRules.Summary}", 3);

            var libCount = tools.Count(t =>
                SchemaLibrary.GetBestSchema(session.ActiveModel, t.Name)?.IsReliable == true);
            if (libCount > 0)
                Emit(ActivityKind.Debug, "Schema library",
                    $"{libCount}/{tools.Count} tool(s) using confirmed schemas.", 3);
        }

        // Build tool-name → payload index map for recording successes later
        var toolSchemaIndex = tools
            .Select((t, i) => (t.Name, i))
            .ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);

        // ── GOBLIN MIND: Format Fingerprint ─────────────────────────────────
        // Read the model's preferred tool-call format from its probe profile.
        // Passed into BuildMessages so the system prompt uses the right format.
        var preferredFormat = ToolCallProfileStore.GetPreferredFormat(session.ActiveModel);
        if (preferredFormat != FormatVariant.BareJson)
            Emit(ActivityKind.Debug, "Format fingerprint",
                $"Using {preferredFormat} tool call format for this model.", 3);

        // Load project rules (global agent + workspace rules merged)
        var rulesText  = await _rules.LoadAsync(session.WorkspaceRoot);
        var globalPath = AgentPresets.GlobalAgentPath;
        var hasGlobal  = File.Exists(globalPath);
        if (!string.IsNullOrEmpty(rulesText))
        {
            var rulesFile  = _rules.FindRulesFile(session.WorkspaceRoot);
            var sourceDesc = hasGlobal && rulesFile != null
                ? $"global_agent.md + {Path.GetFileName(rulesFile)}"
                : hasGlobal ? "global_agent.md" : Path.GetFileName(rulesFile ?? ".agent.md");
            Emit(ActivityKind.Info, "Rules loaded", $"{rulesText.Length} chars ({sourceDesc})");
            // V3: rules content preview
            var preview = rulesText.Length > 600 ? rulesText[..600] + "\n…(truncated)" : rulesText;
            Emit(ActivityKind.Rules, "Rules content", preview, 3);
            // V5: full paths
            if (hasGlobal)
                Emit(ActivityKind.Debug, "Global agent path", globalPath, 5);
            if (rulesFile != null)
                Emit(ActivityKind.Debug, "Workspace rules path", rulesFile, 5);
            OnRulesLoaded?.Invoke(rulesFile);
        }
        else
        {
            OnRulesLoaded?.Invoke(null);
        }

        // Git checkpoint before we touch anything
        var checkpointSha = await _git.CheckpointAsync(session.WorkspaceRoot, "Pre-agent checkpoint");
        if (checkpointSha != null)
        {
            session.LastCheckpointSha = checkpointSha;
            Emit(ActivityKind.Git, "Checkpoint", $"SHA {checkpointSha[..8]}");
        }

        var messages = BuildMessages(session, userPrompt, planOnly: false, rulesText: rulesText, toolFormat: preferredFormat);
        _context.Update(messages);

        var stepCount = 0;
        var finalResponse = "";

        // V3: context usage at start of execute
        Emit(ActivityKind.Debug, "Context", $"{_context.UsedTokens} / {_context.MaxTokens} tokens used ({_context.UsagePercent:F0}%)", 3);

        while (stepCount < profile.MaxSteps && !ct.IsCancellationRequested)
        {
            stepCount++;
            Emit(ActivityKind.Step, $"Step {stepCount}/{profile.MaxSteps}", "Calling model…");

            var pendingToolCalls = new List<ToolCall>();
            var contentBuilder = new System.Text.StringBuilder();

            await foreach (var token in _ollama.StreamCompletionAsync(
                session.ActiveModel, messages,
                tools: toolsPayload.Count > 0 ? toolsPayload : null,
                temperature: profile.Temperature,
                onToolCall: tc => pendingToolCalls.Add(tc),
                onUsage: (p, c) => OnUsage?.Invoke(p, c),
                ct: ct))
            {
                contentBuilder.Append(token);
                OnToken?.Invoke(token);  // Live streaming to UI bubble
            }

            var content = contentBuilder.ToString();
            finalResponse = content;

            // V4: step response snippet
            if (content.Length > 0)
            {
                var snippet4 = content.Length > 300 ? content[..300].Replace("\n", " ").TrimEnd() + "…" : content.Replace("\n", " ");
                Emit(ActivityKind.Debug, $"Step {stepCount} response", $"[{content.Length} chars] {snippet4}", 4);
            }

            // Fallback: some models output tool calls as JSON text instead of
            // structured tool_calls. Parse them so the loop can still execute.
            if (pendingToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(content))
            {
                var textParsed = TryParseTextToolCalls(content);
                if (textParsed.Count > 0)
                {
                    Emit(ActivityKind.Info, "Tool-call parse",
                        $"Detected {textParsed.Count} text-format tool call(s) — executing.");
                    pendingToolCalls.AddRange(textParsed);
                }
            }

            // ── Diagnostic dump so the FlaUI test can see what the model produced ───
            if (!string.IsNullOrEmpty(session.WorkspaceRoot))
            {
                try
                {
                    var diagDir  = Path.Combine(session.WorkspaceRoot, ".orc");
                    Directory.CreateDirectory(diagDir);
                    var diagPath = Path.Combine(diagDir, "_agentlog.txt");
                    var native   = pendingToolCalls.Count(t => !t.IsTextFormat);
                    var textFmt  = pendingToolCalls.Count(t =>  t.IsTextFormat);
                    var snippet  = content.Length > 400 ? content[..400] : content;
                    var diagLine = $"[Step {stepCount}] len={content.Length} native={native} text={textFmt} refusal={IsRefusal(content)}\n{snippet}\n---\n";
                    File.AppendAllText(diagPath, diagLine, System.Text.Encoding.UTF8);
                }
                catch { }
            }

            // Add assistant message to history.
            // If the content is purely text-format tool call JSON (no readable prose),
            // summarise it so the history stays clean rather than full of raw JSON.
            var historyContent = content;
            if (pendingToolCalls.Count > 0 && pendingToolCalls.All(t => t.IsTextFormat))
            {
                var callSummary = string.Join(", ", pendingToolCalls.Select(t => $"{t.Name}(...)"));
                historyContent = string.IsNullOrWhiteSpace(
                    System.Text.RegularExpressions.Regex.Replace(content, @"\{[\s\S]*\}", "").Trim())
                    ? $"[Calling: {callSummary}]"
                    : content;  // keep if model also wrote prose alongside the JSON
            }

            var assistantMsg = new AgentMessage
            {
                Role      = MessageRole.Assistant,
                Content   = historyContent,
                ToolCalls = pendingToolCalls,
                Status    = MessageStatus.Complete
            };
            session.Messages.Add(assistantMsg);
            messages = [.. messages, assistantMsg];
            _context.AddTokens(ContextManager.EstimateTokens(content));

            // No tool calls → check for refusal before accepting as done.
            if (pendingToolCalls.Count == 0)
            {
                // Detect "I'm sorry / I cannot / here's a guide" refusal patterns.
                // Push back once with a hard nudge instead of silently finishing.
                if (IsRefusal(content) && stepCount <= 2)
                {
                    Emit(ActivityKind.Warning, "Refusal detected", "Pushing back — telling model to use tools.");
                    var nudge = new AgentMessage
                    {
                        Role    = MessageRole.User,
                        Content = "STOP. You gave me code in a chat message instead of using the write_file tool. "
                                + "Do NOT put code in markdown blocks. "
                                + "You MUST call write_file for each file RIGHT NOW. "
                                + "Output a JSON tool call on a single line — nothing else. Example:\n"
                                + "{\"name\":\"write_file\",\"arguments\":{\"path\":\"example.py\",\"content\":\"print('hello')\"}}",
                        Status  = MessageStatus.Complete,
                    };
                    session.Messages.Add(nudge);
                    messages = [.. messages, nudge];
                    continue;   // re-enter the loop with the nudge
                }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var preview = content.Length > 120
                        ? content[..120].Replace("\n", " ").TrimEnd() + "…"
                        : content.Replace("\n", " ");
                    Emit(ActivityKind.Info, "Done", preview);
                }
                break;
            }

            // Execute each tool call
            foreach (var tc in pendingToolCalls)
            {
                Emit(ActivityKind.Tool, tc.Name, FormatArgs(tc.Arguments));
                // V3: full untruncated args
                var fullArgs = string.Join(", ", tc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                Emit(ActivityKind.Debug, $"{tc.Name} [full args]", fullArgs, 3);

                var result = await _registry.ExecuteAsync(tc, ct,
                    onActivity: msg => Emit(ActivityKind.Tool, tc.Name, msg));

                // GOBLIN MIND: record schema that produced a successful tool call
                if (!result.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase)
                    && toolSchemaIndex.TryGetValue(tc.Name, out var schemaIdx)
                    && schemaIdx < toolsPayload.Count)
                {
                    var format = ToolCallProfileStore.GetPreferredFormat(session.ActiveModel);
                    _ = SchemaLibrary.RecordSuccessAsync(session.ActiveModel, tc.Name,
                        format, toolsPayload[schemaIdx]);
                }

                Emit(ActivityKind.ToolResult, tc.Name, result.Length > 200 ? result[..200] + "…" : result);
                // V4: full tool result
                if (result.Length > 200)
                    Emit(ActivityKind.Debug, $"{tc.Name} [full result]", result, 4);

                // Auto-verify after write_file
                if (tc.Name == "write_file" && profile.AutoVerify
                    && _registry.TryGet("run_tests", out _))
                {
                    Emit(ActivityKind.Info, "Auto-verify", "Running tests after file write…");
                    var verifyResult = await _registry.ExecuteAsync(
                        new ToolCall { Name = "run_tests", Arguments = [] }, ct);
                    result += $"\n\n[AUTO-VERIFY]\n{verifyResult}";
                    Emit(ActivityKind.Info, "Auto-verify done", verifyResult.Length > 100 ? verifyResult[..100] + "…" : verifyResult);
                }

                // Append tool result to history.
                // Text-format tool calls must get a user-role reply — the model
                // won't understand a "tool" role message because it never used
                // the structured tool_calls API.
                AgentMessage toolMsg;
                if (tc.IsTextFormat)
                {
                    // Ensure the body is never empty — an empty result looks like a
                    // missing response to the model and causes it to retry forever.
                    var body = string.IsNullOrWhiteSpace(result)
                        ? "(tool completed — no output)"
                        : result;
                    toolMsg = new AgentMessage
                    {
                        Role    = MessageRole.User,
                        Content = $"[Tool result: {tc.Name}]\n{body}",
                        Status  = MessageStatus.Complete,
                    };
                }
                else
                {
                    toolMsg = new AgentMessage
                    {
                        Role       = MessageRole.Tool,
                        Content    = result,
                        ToolCallId = tc.Id,
                        Status     = MessageStatus.Complete,
                    };
                }
                session.Messages.Add(toolMsg);
                messages = [.. messages, toolMsg];
                _context.AddTokens(ContextManager.EstimateTokens(result));
            }

            // Context warning
            if (_context.IsCritical)
                Emit(ActivityKind.Warning, "Context critical", $"{_context.UsagePercent:F0}% of {_context.MaxTokens} tokens used");
            else if (_context.IsWarning)
                Emit(ActivityKind.Warning, "Context warning", $"{_context.UsagePercent:F0}% used");
        }

        if (stepCount >= profile.MaxSteps)
            Emit(ActivityKind.Warning, "Step limit reached", $"Stopped at {profile.MaxSteps} steps.");

        session.LastActivityAt = DateTime.UtcNow;
        session.TotalTokensUsed += _context.UsedTokens;
        return finalResponse;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private List<AgentMessage> BuildMessages(
        ProjectSession session,
        string userPrompt,
        bool planOnly,
        string? rulesText = null,
        FormatVariant toolFormat = FormatVariant.BareJson)
    {
        var profile = ModelProfiles.Get(session.ActiveModel);
        var systemPrompt = planOnly
            ? BuildPlanSystemPrompt(profile, rulesText)
            : BuildExecuteSystemPrompt(profile, session.WorkspaceRoot, rulesText, toolFormat);

        var messages = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = systemPrompt }
        };

        messages.AddRange(session.Messages.TakeLast(40));
        messages.Add(new() { Role = MessageRole.User, Content = userPrompt });
        return messages;
    }

    private static string BuildPlanSystemPrompt(ModelProfile profile, string? rules)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are an expert software engineer. Your job is to PLAN, not implement.");
        sb.AppendLine("The user will review your plan before any code is written.");
        sb.AppendLine();
        sb.AppendLine("Produce a structured plan with:");
        sb.AppendLine("1. What files need to be read/understood first");
        sb.AppendLine("2. What changes will be made and why");
        sb.AppendLine("3. Any risks or uncertainties to flag");
        sb.AppendLine("4. Estimated steps needed");
        sb.AppendLine();
        sb.AppendLine("Do NOT write any code. Do NOT call any tools. Just plan.");
        if (!string.IsNullOrEmpty(rules)) { sb.AppendLine(); sb.AppendLine("Project rules:"); sb.AppendLine(rules); }
        return sb.ToString();
    }

    private static string BuildExecuteSystemPrompt(ModelProfile profile, string workspaceRoot, string? rules, FormatVariant toolFormat = FormatVariant.BareJson)
    {
        var prompt = profile.PromptStyle switch
        {
            PromptStyle.Coding => """
                You are an expert software engineer executing a coding task.
                Workflow: explore → search → read → write → verify → finish
                - Always read files before editing them
                - Prefer small, targeted changes over rewrites
                - After writing, state what you changed and why
                - If tests fail, analyze the failure before retrying
                - Be honest when something is uncertain — ask rather than guess
                """,
            PromptStyle.Agent => """
                You are an autonomous software agent executing a multi-step task.
                Workflow: plan → explore → implement → verify end-to-end → report
                - Break large tasks into checkpoints
                - Surface blockers immediately — don't work around unknown requirements
                - After each major step, summarize what was done
                - If the approach isn't working after 3 attempts, stop and report
                """,
            _ => """
                You are a helpful assistant with access to coding tools.
                Be concise and accurate. Ask for clarification when requirements are unclear.
                """
        };

        // Always tell the model exactly where to work
        prompt += $"\n\nWorkspace root: {workspaceRoot}";
        prompt += "\nUse ONLY absolute paths or paths relative to the workspace root when calling file tools.";
        prompt += "\nDo NOT use placeholder values like <file-path> — always use real paths.";
        prompt += "\n\nCRITICAL BEHAVIOUR RULES (never break these):";
        prompt += "\n- NEVER say \"I cannot\", \"I'm sorry\", \"I'm unable\", or give instructions for the user to follow.";
        prompt += "\n- NEVER describe what you would do. DO IT using tool calls.";
        prompt += "\n- To create a .NET project: {\"name\":\"run_shell\",\"arguments\":{\"command\":\"dotnet new winforms -n ProjectName\"}}";
        prompt += "\n- To create files: {\"name\":\"write_file\",\"arguments\":{\"path\":\"...\",\"content\":\"...\"}}";
        prompt += "\n- If a tool does not exist, use write_file + run_shell to achieve the same result.";
        prompt += "\n- The user cannot follow manual instructions — they need working files on disk.";

        // ── GOBLIN MIND: Format-aware tool call instructions ────────────────
        // Use the model's proven preferred format from FormatProbeEngine.
        // Falls back to BareJson (safest default) when no fingerprint exists.
        prompt += FormatProbeEngine.BuildToolFormatSection(toolFormat);

        // Append universal rules that apply regardless of format
        prompt += """


RULES (apply to all tool call formats):
- Do NOT wrap code in ```code blocks```. Put file content inside the tool call.
- Do NOT say "I would write..." or describe what you plan to do. Call the tool directly.
- Escape newlines as \n and quotes as \" inside string values.
- After every tool call, wait for the result before continuing.
- When the task is fully complete, respond with plain text — no more tool calls.
""";

        if (!string.IsNullOrEmpty(rules))
            prompt += $"\n\nProject rules (follow strictly):\n{rules}";

        return prompt;
    }

    /// <summary>
    /// Fallback parser: detects tool calls emitted as plain-text JSON
    /// (e.g. {"name":"write_file","arguments":{...}}) rather than via the
    /// structured tool_calls API field. Handles bare JSON and ```json fences.
    /// </summary>
    private static List<ToolCall> TryParseTextToolCalls(string content)
    {
        var result = new List<ToolCall>();

        // Strip markdown code fences so we can parse the JSON
        var stripped = Regex.Replace(content, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();

        // Walk through the text and extract every top-level JSON object
        int i = 0;
        while (i < stripped.Length)
        {
            var start = stripped.IndexOf('{', i);
            if (start < 0) break;

            // Find the matching closing brace (handle nesting)
            int depth = 0, end = -1;
            bool inString = false;
            for (int j = start; j < stripped.Length; j++)
            {
                var ch = stripped[j];
                if (ch == '"' && (j == 0 || stripped[j - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
            }

            if (end < 0) break;

            var json = stripped[start..(end + 1)];
            try
            {
                var node = JsonNode.Parse(json);

                // Accept "name", "tool", or "function" as the tool-name key
                var name = node?["name"]?.GetValue<string>()
                        ?? node?["tool"]?.GetValue<string>()
                        ?? node?["function"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(name))
                {
                    // Accept "arguments", "args", "parameters", "inputs" as arg wrappers.
                    // If none present, treat all non-name keys as the args (flat format).
                    var argsNode = node?["arguments"]
                                ?? node?["args"]
                                ?? node?["parameters"]
                                ?? node?["inputs"];

                    var args = new Dictionary<string, object?>();
                    var argsObj = argsNode as JsonObject
                               ?? (argsNode == null
                                   ? node!.AsObject()
                                       .Where(kv => kv.Key is not ("name" or "tool" or "function"))
                                       .Aggregate(new JsonObject(), (acc, kv) =>
                                       {
                                           acc[kv.Key] = kv.Value?.DeepClone();
                                           return acc;
                                       })
                                   : null);

                    if (argsObj != null)
                        foreach (var kvp in argsObj)
                        {
                            // Prefer string; use ToJsonString() for numbers/bools so
                            // integers like 17 come back as "17" not null.
                            args[kvp.Key] = kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                                ? s
                                : kvp.Value?.ToJsonString() ?? "";
                        }

                    result.Add(new ToolCall
                    {
                        Id           = Guid.NewGuid().ToString("N")[..8],
                        Name         = name,
                        Arguments    = args,
                        IsTextFormat = true,   // result must be injected as user message
                    });
                }
            }
            catch { /* malformed JSON — skip */ }

            i = end + 1;
        }

        return result;
    }

    /// <summary>
    /// Remove ```json blocks from a plan that contain fake tool-call shapes
    /// like {"name":"create_project","arguments":{...}}.
    /// These are planning artifacts — not real tool calls — and confuse Execute.
    /// </summary>
    private static string StripFakeToolBlocks(string plan)
    {
        // Match fenced code blocks (```json or ```) whose content looks like a tool call
        var stripped = Regex.Replace(
            plan,
            @"```(?:json)?\s*\r?\n\s*\{\s*""name""\s*:[\s\S]*?\}\s*\r?\n```",
            "",
            RegexOptions.IgnoreCase);

        // Also strip bare (unfenced) JSON objects that are tool-call shaped,
        // sitting on their own line (common when model forgets the fence)
        stripped = Regex.Replace(
            stripped,
            @"(?m)^\s*\{\s*""name""\s*:\s*""[^""]+""[\s\S]*?\}\s*$",
            "",
            RegexOptions.Multiline);

        // Collapse extra blank lines left behind
        stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n").Trim();
        return stripped;
    }

    /// <summary>
    /// Returns true when the model responded with a refusal, instructional text,
    /// or code-in-chat (markdown blocks) instead of using a tool call.
    /// All of these cases warrant a nudge to use write_file directly.
    /// </summary>
    private static bool IsRefusal(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var lower = content.ToLowerInvariant();

        // Classic refusal patterns
        if (lower.Contains("i'm sorry")
            || lower.Contains("i am sorry")
            || lower.Contains("i cannot")
            || lower.Contains("i can't")
            || lower.Contains("i'm unable")
            || lower.Contains("i am unable")
            || lower.Contains("as an ai")
            || lower.Contains("step-by-step guide")
            || lower.Contains("guide you through")
            || lower.Contains("here's how you can")
            || lower.Contains("here is how you can")
            || lower.Contains("open visual studio")
            || lower.Contains("manually implement"))
            return true;

        // Code-in-chat: model provided code as markdown blocks instead of calling write_file.
        // Detected by: code fences containing actual code keywords.
        // This is a very common failure mode — the nudge gets the model to switch to tool calls.
        if (content.Contains("```"))
        {
            if (lower.Contains("```python") || lower.Contains("```py"))
                return true;
            // Generic code fence containing import/def/class/function keywords
            if (lower.Contains("```") && (
                lower.Contains("import ") || lower.Contains("def ") ||
                lower.Contains("class ") || lower.Contains("function ") ||
                lower.Contains("public ") || lower.Contains("private ")))
                return true;
        }

        return false;
    }

    private static string FormatArgs(Dictionary<string, object?> args)
    {
        var parts = args.Take(2).Select(kv =>
            $"{kv.Key}={kv.Value?.ToString()?[..Math.Min(40, kv.Value?.ToString()?.Length ?? 0)]}");
        return string.Join(", ", parts);
    }

    private void Emit(ActivityKind kind, string label, string detail, int verbosity = 2)
        => Activity?.Invoke(new ActivityEvent(kind, label, detail, DateTime.UtcNow, verbosity));
}

// ── Activity event model ─────────────────────────────────────────────────────

/// <summary>
/// Verbosity levels for activity events:
///   1 = Silent   — nothing shown
///   2 = Default  — current behavior (model switches, tool calls, warnings)
///   3 = Medium   — + rules content preview, full tool args, context usage
///   4 = High     — + step response snippets, full tool results, agent file content
///   5 = Everything — raw sizes, timing, full untruncated content
/// </summary>
public enum ActivityKind { Info, Step, Tool, ToolResult, Git, Warning, Error, Rules, Debug }

public record ActivityEvent(ActivityKind Kind, string Label, string Detail, DateTime Timestamp, int Verbosity = 2)
{
    public string Icon => Kind switch
    {
        ActivityKind.Step       => "▶",
        ActivityKind.Tool       => "⚙",
        ActivityKind.ToolResult => "✓",
        ActivityKind.Git        => "⬡",
        ActivityKind.Warning    => "⚠",
        ActivityKind.Error      => "✗",
        ActivityKind.Rules      => "📋",
        ActivityKind.Debug      => "🔍",
        _                       => "·"
    };

    /// <summary>Color hex for the icon column, by kind.</summary>
    public string IconColor => Kind switch
    {
        ActivityKind.Warning    => "#CCA700",
        ActivityKind.Error      => "#F44747",
        ActivityKind.Rules      => "#9CDCFE",
        ActivityKind.Git        => "#C586C0",
        ActivityKind.Tool       => "#4EC9B0",
        ActivityKind.ToolResult => "#4EC9B0",
        ActivityKind.Step       => "#569CD6",
        ActivityKind.Debug      => "#808080",
        _                       => "#569CD6"
    };

    /// <summary>True if Detail looks like an absolute file path on Windows.</summary>
    public bool HasFilePath => Detail.Length > 3 &&
        (Detail.Contains(":\\") || Detail.Contains(":/"));
}
