using System.Text;

namespace Oscova.Agents.Tasks;

/// <summary>Formats enum-like values as task-file YAML tokens.</summary>
public static class TaskFileValueFormatter
{
    /// <summary>Converts PascalCase names to the snake_case tokens stored in task files.</summary>
    public static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
