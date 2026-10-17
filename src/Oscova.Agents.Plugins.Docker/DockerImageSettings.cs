namespace Oscova.Agents.Plugins.Docker;
/// <summary>Stores the shared nonsecret Dockerfile inside this plugin's installation namespace.</summary>
public sealed class DockerImageSettings(IPluginSettings settings)
{
    public const string LocalKey = "image";
    public string CustomDockerfile { get; private set; } = string.Empty;
    public bool HasCustomDockerfile => !string.IsNullOrWhiteSpace(CustomDockerfile);
    /// <summary>Reads the current installation-local Dockerfile without probing Docker.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
        => CustomDockerfile = await settings.ReadAsync(LocalKey, ct).ConfigureAwait(false) ?? string.Empty;
    /// <summary>Validates instructions before persisting through the authenticated settings adapter.</summary>
    public async Task SaveAsync(IPluginSettings bound, string content, CancellationToken ct = default)
    {
        Validate(content);
        await bound.WriteAsync(LocalKey, content, ct).ConfigureAwait(false);
        CustomDockerfile = content;
    }
    /// <summary>Rejects nonempty build instructions without a base image declaration.</summary>
    internal static void Validate(string content)
    {
        if (!string.IsNullOrWhiteSpace(content) && !content.Split('\n').Any(line => line.TrimStart().StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)))
        { throw new ArgumentException("Dockerfile must contain a FROM instruction.", nameof(content)); }
    }
}
