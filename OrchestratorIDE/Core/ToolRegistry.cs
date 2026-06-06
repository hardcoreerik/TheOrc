using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Central registry of available tools. Filters to active toolset per model profile.
///
/// Layer 1 — rich "tool not found" messages fed back to the model so it
///            can self-correct on the next step.
/// Layer 2 — optional OnUnknownTool hook: MainWindow wires this to the
///            UnknownToolCard UI so the user can choose how to handle it.
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = [];
    private readonly Trust.ApprovalQueue _approvalQueue;

    // ── Layer 2 hook ──────────────────────────────────────────────────────
    /// <summary>
    /// If set, called whenever the model invokes an unregistered tool.
    /// Returns the string to feed back to the model as the tool result.
    /// When null, falls back to the built-in rich error message (Layer 1).
    /// </summary>
    public Func<ToolCall, Task<string>>? OnUnknownTool { get; set; }

    /// <summary>
    /// Wired by MainWindow to the UserInputDialog.
    /// Called when the agent invokes ask_user(question).
    /// Should show a modal dialog and return the user's answer string.
    /// If null, ask_user returns a static "[No UI]" message.
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? OnAskUser { get; set; }

    public ToolRegistry(Trust.ApprovalQueue approvalQueue)
    {
        _approvalQueue = approvalQueue;
    }

    public void Register(ToolDefinition tool) => _tools[tool.Name] = tool;

    public IReadOnlyList<ToolDefinition> GetForProfile(ModelProfile profile)
    {
        var allowed = profile.ToolSet switch
        {
            ToolSet.Minimal => new[] { "read_file", "list_files", "run_shell" },
            ToolSet.Coding  => new[] { "read_file", "write_file", "list_files", "run_shell", "grep_code", "get_outline", "run_tests", "fetch_url" },
            ToolSet.Full    => _tools.Keys.ToArray(),
            _ => []
        };
        return _tools.Values.Where(t => allowed.Contains(t.Name)).ToList();
    }

    public bool TryGet(string name, out ToolDefinition? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyList<string> GetRegisteredNames() => [.. _tools.Keys.OrderBy(k => k)];

    // ── Execute ───────────────────────────────────────────────────────────

    /// <summary>
    /// Execute a tool call, routing through the approval queue if required.
    /// Returns the tool result string.
    /// </summary>
    public async Task<string> ExecuteAsync(
        ToolCall call,
        CancellationToken ct,
        Action<string>? onActivity = null)
    {
        // ── Layer 1 / 2: unknown tool ─────────────────────────────────────
        if (!_tools.TryGetValue(call.Name, out var def))
        {
            call.Status = ToolCallStatus.Failed;

            // Layer 2: show UI card if wired up
            if (OnUnknownTool != null)
                return await OnUnknownTool(call);

            // Layer 1: rich self-correction message (fallback / autotest)
            return BuildRichNotFoundMessage(call, [.. _tools.Keys]);
        }

        // ── Normal execution ──────────────────────────────────────────────
        if (def.RequiresApproval || call.RequiresApproval)
        {
            call.Status = ToolCallStatus.AwaitingApproval;
            onActivity?.Invoke($"⏸ Awaiting approval: {call.Name}({FormatArgs(call.Arguments)})");

            var approved = await _approvalQueue.RequestApprovalAsync(call, ct);
            if (!approved)
            {
                call.Status = ToolCallStatus.Rejected;
                return "[REJECTED] User denied this action.";
            }
        }

        call.Status    = ToolCallStatus.Running;
        call.StartedAt = DateTime.UtcNow;
        onActivity?.Invoke($"▶ {call.Name}({FormatArgs(call.Arguments)})");

        try
        {
            var result = await def.Handler!(call.Arguments, ct);
            call.Status      = ToolCallStatus.Complete;
            call.Result      = result;
            call.CompletedAt = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            call.Status = ToolCallStatus.Failed;
            call.Result = $"[ERROR] {ex.Message}";
            return call.Result;
        }
    }

    // ── Layer 1: rich not-found message ───────────────────────────────────

    /// <summary>
    /// Builds a detailed error message the model can use to self-correct:
    /// lists every available tool with a short purpose, and adds a
    /// context-aware hint based on what the unknown tool was trying to do.
    /// </summary>
    public static string BuildRichNotFoundMessage(ToolCall call, IEnumerable<string> registered)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Tool not found: {call.Name}]");
        sb.AppendLine();
        sb.AppendLine($"'{call.Name}' is not a registered tool. You must only call tools from this list:");
        sb.AppendLine();

        // Always describe the core tools regardless of what's registered,
        // so the model has a reliable vocabulary to fall back on.
        sb.AppendLine("  write_file(path, content)       — create or overwrite any file on disk");
        sb.AppendLine("  read_file(path)                 — read a file's full contents");
        sb.AppendLine("  list_files(path, depth?)        — browse workspace directory tree");
        sb.AppendLine("  run_shell(command)              — run ANY shell command (dotnet, npm, git, python…)");
        sb.AppendLine("  grep_code(pattern, path?)       — search file contents with regex");
        sb.AppendLine("  run_tests()                     — build the project and run its test suite");
        sb.AppendLine("  fetch_url(url)                  — download a web page or call an API");
        sb.AppendLine();

        // Context-aware hint: map common hallucinated tool names → real equivalents
        var hint = BuildHint(call);
        if (hint != null)
        {
            sb.AppendLine("Hint for this specific request:");
            sb.AppendLine(hint);
            sb.AppendLine();
        }

        sb.AppendLine("Use one of the tools above to accomplish the same goal. Output a JSON tool call — no explanations.");
        return sb.ToString().TrimEnd();
    }

    private static string? BuildHint(ToolCall call)
    {
        var name = call.Name.ToLowerInvariant();
        var args = call.Arguments;

        // Project scaffolding
        if (name is "create_project" or "scaffold_project" or "new_project" or "init_project")
        {
            var proj   = args.TryGetValue("project_name", out var pn) ? pn?.ToString() ?? "MyApp" : "MyApp";
            var type   = args.TryGetValue("project_type",  out var pt) ? pt?.ToString() ?? "" : "";
            var tmpl   = type.ToLowerInvariant() switch
            {
                var t when t.Contains("winforms") || t.Contains("windows forms") => "winforms",
                var t when t.Contains("wpf")                                      => "wpf",
                var t when t.Contains("console")                                  => "console",
                var t when t.Contains("web") || t.Contains("asp")                 => "web",
                var t when t.Contains("blazor")                                   => "blazor",
                _                                                                 => "console",
            };
            return $"  → {{\"name\":\"run_shell\",\"arguments\":{{\"command\":\"dotnet new {tmpl} -n {proj}\"}}}}";
        }

        // UI/form design — just write the code file
        if (name is "design_form" or "create_form" or "add_control" or "design_ui")
            return "  → Use write_file to create the .cs or .xaml file with the UI code directly.";

        // Event handlers — write the code
        if (name is "implement_event_handlers" or "add_event_handler" or "wire_events")
            return "  → Use write_file to write the event handler code into the relevant .cs file.";

        // Running / testing
        if (name is "run_application" or "run_app" or "execute_app" or "start_app")
            return "  → {\"name\":\"run_shell\",\"arguments\":{\"command\":\"dotnet run\"}}";

        if (name is "test_application" or "run_unit_tests" or "run_all_tests")
            return "  → {\"name\":\"run_tests\",\"arguments\":{}}";

        // Build
        if (name is "build_project" or "compile" or "build")
            return "  → {\"name\":\"run_shell\",\"arguments\":{\"command\":\"dotnet build\"}}";

        // Install packages
        if (name is "install_package" or "add_package" or "nuget_install")
        {
            var pkg = args.TryGetValue("package", out var p) ? p?.ToString() ?? "PackageName" : "PackageName";
            return $"  → {{\"name\":\"run_shell\",\"arguments\":{{\"command\":\"dotnet add package {pkg}\"}}}}";
        }

        // Deploy / publish
        if (name is "deploy" or "publish" or "publish_app")
            return "  → {\"name\":\"run_shell\",\"arguments\":{\"command\":\"dotnet publish -c Release\"}}";

        // Generic file creation
        if (name is "create_file" or "make_file" or "touch")
        {
            var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "file.txt" : "file.txt";
            return $"  → {{\"name\":\"write_file\",\"arguments\":{{\"path\":\"{path}\",\"content\":\"\"}}}}";
        }

        return null;  // No specific hint — generic message is enough
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string FormatArgs(Dictionary<string, object?> args)
    {
        var parts = args.Take(2).Select(kv =>
            $"{kv.Key}={kv.Value?.ToString()?[..Math.Min(30, kv.Value?.ToString()?.Length ?? 0)]}");
        return string.Join(", ", parts);
    }
}
