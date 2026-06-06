namespace OrchestratorIDE.Trust;

/// <summary>
/// Auto-discovers and loads .agent.md / AGENT.md / .clinerules from the project root.
/// The content is injected into the system prompt so the agent always knows project conventions.
/// </summary>
public class RulesLoader
{
    private static readonly string[] _candidates =
    [
        ".agent.md", "AGENT.md", ".clinerules", "CLAUDE.md", ".rules.md"
    ];

    public async Task<string> LoadAsync(string workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return "";

        foreach (var name in _candidates)
        {
            var path = Path.Combine(workspaceRoot, name);
            if (File.Exists(path))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(path);
                    return $"[Rules from {name}]\n{content.Trim()}";
                }
                catch { /* skip unreadable files */ }
            }
        }
        return "";
    }

    public string? FindRulesFile(string workspaceRoot)
    {
        foreach (var name in _candidates)
        {
            var path = Path.Combine(workspaceRoot, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string DefaultTemplate(string projectName) => $"""
        # Agent Rules — {projectName}

        ## Code Style
        - Follow existing conventions in the codebase
        - Prefer small, targeted changes over full rewrites

        ## Testing
        - Write or update tests for all new functionality
        - Run tests before declaring a task complete

        ## Safety
        - Never delete files without explicit instruction
        - Always show a diff before writing to existing files
        - Ask before making architectural decisions

        ## Communication
        - State what you're about to do before doing it
        - If uncertain, ask — don't guess
        - Flag risks and blockers immediately
        """;
}
