using Oscova.Agents.Credentials;
namespace Oscova.Agents.Core.Secrets.Credentials;

/// <summary>Pre-built field sets so consumers do not hand-build credential field lists.</summary>
public static class CredentialFieldSets
{
    /// <summary>Field name used by every free-form credential.</summary>
    public const string FreeFormFieldName = "value";

    /// <summary>Single secret field for an API-key style credential.</summary>
    public static IReadOnlyList<CredentialFieldDefinition> ApiKey(string label = "API Key", string? defaultEnvVariable = null)
        => [new() { Name = "apiKey", Label = label, IsSecret = true, IsRequired = true, DefaultEnvVariable = defaultEnvVariable }];

    /// <summary>Single secret field for a bot token.</summary>
    public static IReadOnlyList<CredentialFieldDefinition> BotToken(string label = "Bot Token")
        => [new() { Name = "botToken", Label = label, IsSecret = true, IsRequired = true }];

    /// <summary>The fixed single-secret-field shape used by user-created credentials.</summary>
    public static IReadOnlyList<CredentialFieldDefinition> FreeForm()
        => [new() { Name = FreeFormFieldName, Label = "Value", IsSecret = true, IsRequired = true }];

}
