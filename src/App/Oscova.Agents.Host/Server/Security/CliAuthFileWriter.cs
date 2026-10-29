using System.Text.Json;
using Oscova.Agents.Core.Cli;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>
/// Writes same-OS-user CLI credentials after first admin setup.
/// </summary>
public sealed class CliAuthFileWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task WriteLocalTokenAsync(string host, CliAccessTokenResult token, CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".oscova");
        CliFileSecurity.EnsurePrivateDirectory(root);

        var path = Path.Combine(root, "auth.json");
        var normalizedHost = NormalizeHost(host);
        var file = await ReadExistingAsync(path, ct).ConfigureAwait(false) ?? new CliAuthFile();
        file.Version = 1;
        for (var index = file.Hosts.Count - 1; index >= 0; index--)
        {
            if (string.Equals(NormalizeHost(file.Hosts[index].Host), normalizedHost, StringComparison.OrdinalIgnoreCase))
            {
                file.Hosts.RemoveAt(index);
            }
        }

        file.Hosts.Add(new CliAuthHostEntry
        {
            Host = normalizedHost,
            Token = token.Token,
            IdentityUserId = token.UserId,
            IdentityUserName = token.UserName,
            IssuedAtUtc = token.IssuedAtUtc,
            ExpiresAtUtc = null,
            Scopes = ["cli"]
        });
        file.CurrentHost = normalizedHost;

        var tempPath = path + ".tmp";
        var stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, file, JsonOptions, ct).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static async Task<CliAuthFile?> ReadExistingAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<CliAuthFile>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeHost(string host)
    {
        var trimmed = string.IsNullOrWhiteSpace(host) ? CliHostUrlSelector.DefaultLocalHost : host.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString().TrimEnd('/')
            : trimmed;
    }
}
