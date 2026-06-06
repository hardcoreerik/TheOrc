using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Central registry of available tools. Filters to active toolset per model profile.
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = [];
    private readonly Trust.ApprovalQueue _approvalQueue;

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

    /// <summary>
    /// Execute a tool call, routing through the approval queue if required.
    /// Returns the tool result string.
    /// </summary>
    public async Task<string> ExecuteAsync(
        ToolCall call,
        CancellationToken ct,
        Action<string>? onActivity = null)
    {
        if (!_tools.TryGetValue(call.Name, out var def))
            return $"[ERROR] Unknown tool: {call.Name}";

        // Route through approval if needed
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

        call.Status = ToolCallStatus.Running;
        call.StartedAt = DateTime.UtcNow;
        onActivity?.Invoke($"▶ {call.Name}({FormatArgs(call.Arguments)})");

        try
        {
            var result = await def.Handler!(call.Arguments, ct);
            call.Status = ToolCallStatus.Complete;
            call.Result = result;
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

    private static string FormatArgs(Dictionary<string, object?> args)
    {
        var parts = args.Take(2).Select(kv => $"{kv.Key}={kv.Value?.ToString()?[..Math.Min(30, kv.Value?.ToString()?.Length ?? 0)]}");
        return string.Join(", ", parts);
    }
}
