using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Tracks token usage and trims history to stay within model context limits.
/// Rough estimate: 1 token ≈ 4 characters (standard heuristic).
/// </summary>
public class ContextManager
{
    private readonly int _maxTokens;
    private int _usedTokens;

    public int MaxTokens => _maxTokens;
    public int UsedTokens => _usedTokens;
    public double UsagePercent => _maxTokens > 0 ? (double)_usedTokens / _maxTokens * 100 : 0;
    public bool IsWarning => UsagePercent >= 70;
    public bool IsCritical => UsagePercent >= 85;

    public event Action? UsageChanged;

    public ContextManager(int maxTokens = 32_768)
    {
        _maxTokens = maxTokens;
    }

    public void Update(IEnumerable<AgentMessage> messages)
    {
        _usedTokens = messages.Sum(m => EstimateTokens(m.Content));
        UsageChanged?.Invoke();
    }

    public void AddTokens(int count)
    {
        _usedTokens += count;
        UsageChanged?.Invoke();
    }

    /// <summary>
    /// Trim oldest non-system messages to bring usage under 70%.
    /// Always preserves: system message, last 2 user+assistant pairs.
    /// </summary>
    public List<AgentMessage> TrimToFit(List<AgentMessage> messages)
    {
        var trimmed = messages.ToList();

        while (UsagePercent > 70 && trimmed.Count > 4)
        {
            // Find first non-system message beyond the first to trim
            var idx = trimmed.FindIndex(1, m => m.Role != MessageRole.System);
            if (idx < 0) break;
            var removed = trimmed[idx];
            trimmed.RemoveAt(idx);
            _usedTokens -= EstimateTokens(removed.Content);
        }

        UsageChanged?.Invoke();
        return trimmed;
    }

    public static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);
}
