using OrchestratorIDE.Models;

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
        var messages = BuildMessages(session, userPrompt, planOnly: true);

        var planText = new System.Text.StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            session.ActiveModel, messages,
            tools: null,   // no tools in plan mode
            temperature: profile.Temperature,
            ct: ct))
        {
            planText.Append(token);
            OnToken?.Invoke(token);  // Live streaming to UI bubble
        }

        var plan = planText.ToString().Trim();
        session.PlanText = plan;
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
        var tools = _registry.GetForProfile(profile);

        // Load project rules
        var rulesText = await _rules.LoadAsync(session.WorkspaceRoot);
        if (!string.IsNullOrEmpty(rulesText))
            Emit(ActivityKind.Info, "Rules loaded", $"{rulesText.Length} chars from .agent.md");

        // Git checkpoint before we touch anything
        var checkpointSha = await _git.CheckpointAsync(session.WorkspaceRoot, "Pre-agent checkpoint");
        if (checkpointSha != null)
        {
            session.LastCheckpointSha = checkpointSha;
            Emit(ActivityKind.Git, "Checkpoint", $"SHA {checkpointSha[..8]}");
        }

        var messages = BuildMessages(session, userPrompt, planOnly: false, rulesText: rulesText);
        _context.Update(messages);

        var stepCount = 0;
        var finalResponse = "";

        while (stepCount < profile.MaxSteps && !ct.IsCancellationRequested)
        {
            stepCount++;
            Emit(ActivityKind.Step, $"Step {stepCount}/{profile.MaxSteps}", "Calling model…");

            var pendingToolCalls = new List<ToolCall>();
            var contentBuilder = new System.Text.StringBuilder();

            await foreach (var token in _ollama.StreamCompletionAsync(
                session.ActiveModel, messages, tools,
                temperature: profile.Temperature,
                onToolCall: tc => pendingToolCalls.Add(tc),
                ct: ct))
            {
                contentBuilder.Append(token);
                OnToken?.Invoke(token);  // Live streaming to UI bubble
            }

            var content = contentBuilder.ToString();
            finalResponse = content;

            // Add assistant message to history
            var assistantMsg = new AgentMessage
            {
                Role = MessageRole.Assistant,
                Content = content,
                ToolCalls = pendingToolCalls,
                Status = MessageStatus.Complete
            };
            session.Messages.Add(assistantMsg);
            messages = [.. messages, assistantMsg];
            _context.AddTokens(ContextManager.EstimateTokens(content));

            // No tool calls → done
            if (pendingToolCalls.Count == 0) break;

            // Execute each tool call
            foreach (var tc in pendingToolCalls)
            {
                Emit(ActivityKind.Tool, tc.Name, FormatArgs(tc.Arguments));

                var result = await _registry.ExecuteAsync(tc, ct,
                    onActivity: msg => Emit(ActivityKind.Tool, tc.Name, msg));

                Emit(ActivityKind.ToolResult, tc.Name, result.Length > 200 ? result[..200] + "…" : result);

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

                // Append tool result to history
                var toolMsg = new AgentMessage
                {
                    Role = MessageRole.Tool,
                    Content = result,
                    ToolCallId = tc.Id,
                    Status = MessageStatus.Complete
                };
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
        string? rulesText = null)
    {
        var profile = ModelProfiles.Get(session.ActiveModel);
        var systemPrompt = planOnly
            ? BuildPlanSystemPrompt(profile, rulesText)
            : BuildExecuteSystemPrompt(profile, rulesText);

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

    private static string BuildExecuteSystemPrompt(ModelProfile profile, string? rules)
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

        if (!string.IsNullOrEmpty(rules))
            prompt += $"\n\nProject rules (follow strictly):\n{rules}";

        return prompt;
    }

    private static string FormatArgs(Dictionary<string, object?> args)
    {
        var parts = args.Take(2).Select(kv =>
            $"{kv.Key}={kv.Value?.ToString()?[..Math.Min(40, kv.Value?.ToString()?.Length ?? 0)]}");
        return string.Join(", ", parts);
    }

    private void Emit(ActivityKind kind, string label, string detail)
        => Activity?.Invoke(new ActivityEvent(kind, label, detail, DateTime.UtcNow));
}

// ── Activity event model ─────────────────────────────────────────────────────

public enum ActivityKind { Info, Step, Tool, ToolResult, Git, Warning, Error }

public record ActivityEvent(ActivityKind Kind, string Label, string Detail, DateTime Timestamp)
{
    public string Icon => Kind switch
    {
        ActivityKind.Step => "▶",
        ActivityKind.Tool => "⚙",
        ActivityKind.ToolResult => "✓",
        ActivityKind.Git => "⬡",
        ActivityKind.Warning => "⚠",
        ActivityKind.Error => "✗",
        _ => "·"
    };
}
