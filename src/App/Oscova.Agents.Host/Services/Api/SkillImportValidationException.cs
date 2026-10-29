namespace Oscova.Agents.Host.Services.Api;

/// <summary>
/// Carries server-side skill import validation details for logs while the UI shows friendlier copy.
/// </summary>
public sealed class SkillImportValidationException(string message) : Exception(message)
{
}
