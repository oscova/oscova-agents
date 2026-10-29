using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.Secrets.Credentials;

/// <summary>Where a credential field gets its value from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CredentialFieldSource>))]
public enum CredentialFieldSource
{
    /// <summary>The value is stored on the credential row (plain or encrypted).</summary>
    [JsonStringEnumMemberName("stored")]
    Stored,

    /// <summary>The value is read from an environment variable at resolve time.</summary>
    [JsonStringEnumMemberName("env")]
    Env
}
