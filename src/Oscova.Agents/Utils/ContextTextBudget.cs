using System.Text;

namespace Oscova.Agents.Utils;

/// <summary>
/// Truncates prompt context text to a small approximate token budget.
/// </summary>
public static class ContextTextBudget
{
    /// <summary>
    /// Keeps leading lines until the approximate token budget is exhausted.
    /// </summary>
    public static string Truncate(string content, int tokenBudget)
    {
        if (string.IsNullOrWhiteSpace(content) || tokenBudget <= 0)
        {
            return string.Empty;
        }

        if (AgentTokenCounter.CountTokens(content) <= tokenBudget)
        {
            return content;
        }

        var builder = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            var candidate = builder + line + "\n";
            if (AgentTokenCounter.CountTokens(candidate) > tokenBudget)
            {
                break;
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }
}
