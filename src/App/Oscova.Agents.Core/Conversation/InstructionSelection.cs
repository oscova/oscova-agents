namespace Oscova.Agents.Core.Conversation;

/// <summary>Selects a managed instruction without interpreting its default or custom text.</summary>
public static class InstructionSelection
{
    public const string Default = "default";
    public const string Append = "append";
    public const string Replace = "replace";

    /// <summary>Rejects composition modes that this application does not support.</summary>
    public static void Validate(string? mode)
    {
        if (mode is not Default and not Append and not Replace)
        {
            throw new InvalidOperationException("Instruction mode must be default, append, or replace.");
        }
    }

    /// <summary>Returns the selected instruction while preserving custom text exactly.</summary>
    public static string Select(string? mode, string customText, string? defaultText)
    {
        Validate(mode);
        if (mode == Replace) { return customText; }

        if (defaultText is null)
        {
            throw new InvalidOperationException("The instruction default is unavailable.");
        }

        return mode == Append ? defaultText + "\n\n" + customText : defaultText;
    }
}
