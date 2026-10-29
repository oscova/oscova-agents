namespace Oscova.Agents.Core.Secrets;

public class UserCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;

    /// <summary>User whose confidential-data key protects the stored secret fields.</summary>
    public string? ProtectionUserId { get; set; }

    public string CredentialKey { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>JSON field set keyed by field name; replaces the single encrypted value.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>Hides system-written rows (OAuth tokens, provider secrets) from the user UI.</summary>
    public bool IsManaged { get; set; }

    /// <summary>Identifies a durable OAuth provider ownership claim.</summary>
    public string? OAuthProviderId { get; set; }

    /// <summary>Identifies the plugin that first claimed the provider.</summary>
    public string? OwnerPluginId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
