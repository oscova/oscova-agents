using Scriban;

namespace Oscova.Agents.Templating;

/// <summary>
/// Renders Scriban template placeholders in prompt strings.
/// </summary>
public static class PromptTemplateRenderer
{
    /// <summary>
    /// Parses and renders a Scriban template with the given model.
    /// Returns the original text unchanged if parsing fails.
    /// </summary>
    public static string Render(string template, object model)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{"))
        {
            return template;
        }

        var parsed = Template.Parse(template);
        if (parsed.HasErrors)
        {
            return template;
        }

        return parsed.Render(model, member => member.Name);
    }
}
