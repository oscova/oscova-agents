using Oscova.Agents.Credentials;
using Oscova.Agents.Plugins;
namespace Oscova.Agents.Core.Secrets.Credentials;

/// <summary>Holds every code-declared credential slot, built once at startup.</summary>
public interface ICredentialTypeRegistry
{
    /// <summary>All declared credential slots.</summary>
    IReadOnlyList<CredentialDeclaration> All { get; }

    /// <summary>Finds the declaration for a credential key, or null when the key is free-form.</summary>
    CredentialDeclaration? Find(string credentialKey);

    /// <summary>Finds the plugin that owns a stored credential declaration.</summary>
    LoadedAgentPlugin? GetOwner(string storedKey);
}
