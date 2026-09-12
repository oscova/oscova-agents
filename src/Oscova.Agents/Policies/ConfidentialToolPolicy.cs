using Microsoft.Extensions.AI;

namespace Oscova.Agents.Policies;

/// <summary>Evaluates reflected tool confidentiality markers at runtime.</summary>
public static class ConfidentialToolPolicy
{
    /// <summary>Returns whether a tool may be exposed to the effective agent confidentiality mode.</summary>
    public static bool IsAvailable(AITool tool, bool isConfidential)
    {
        if (tool is not AIFunction function || function.UnderlyingMethod is not { } method)
        {
            return true;
        }

        var confidentialOnly = method.IsDefined(typeof(ConfidentialAgentOnlyAttribute), inherit: false) ||
                               method.DeclaringType?.IsDefined(typeof(ConfidentialAgentOnlyAttribute), inherit: false) == true;
        var nonConfidentialOnly = method.IsDefined(typeof(NonConfidentialAgentOnlyAttribute), inherit: false) ||
                                  method.DeclaringType?.IsDefined(typeof(NonConfidentialAgentOnlyAttribute), inherit: false) == true;

        if (confidentialOnly && nonConfidentialOnly)
        {
            return false;
        }

        return confidentialOnly ? isConfidential : !nonConfidentialOnly || !isConfidential;
    }
}
