using System.Text.Json;
using Oscova.Agents.Core.Cli;

namespace Oscova.Agents.App.Cli;

public sealed class CliAuthFileStore(CliPaths paths)
{
    public async Task<CliAuthFile?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.AuthFilePath))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(paths.AuthFilePath);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<CliAuthFile>(stream, CliJson.Options, cancellationToken).ConfigureAwait(false);
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

    public async Task WriteAsync(CliAuthFile file, CancellationToken cancellationToken)
    {
        paths.EnsureRootDirectory();
        var tempPath = paths.AuthFilePath + ".tmp";
        var stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, file, CliJson.Options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, paths.AuthFilePath, overwrite: true);
    }

    public async Task UpsertAsync(string host, CliAuthHostEntry entry, CancellationToken cancellationToken)
    {
        host = CliHostResolver.NormalizeHostUrl(host);
        entry.Host = host;
        var file = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CliAuthFile();
        RemoveHosts(file.Hosts, host);
        file.Hosts.Add(entry);
        file.CurrentHost = host;
        await WriteAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the persisted auth entry for a normalized host, if the CLI is already signed in there.
    /// </summary>
    public async Task<CliAuthHostEntry?> FindAsync(string host, CancellationToken cancellationToken)
    {
        var normalizedHost = CliHostResolver.NormalizeHostUrl(host);
        var file = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (file?.Hosts is null)
        {
            return null;
        }

        return file.Hosts.FirstOrDefault(entry => HostMatches(entry.Host, normalizedHost));
    }

    /// <summary>Finds the selected account's node, or the host's sole enrollment when signed out.</summary>
    public async Task<CliNodeEntry?> FindNodeAsync(string host, string? userId, CancellationToken ct)
    {
        var normalizedHost = CliHostResolver.NormalizeHostUrl(host);
        var file = await ReadAsync(ct).ConfigureAwait(false);
        var matches = file?.Nodes.Where(node => HostMatches(node.Host, normalizedHost)
            && (userId is null || node.UserId == userId)).ToArray() ?? [];
        if (matches.Length > 1)
        {
            throw new CliExitException(4, "More than one account has a node enrolled for this host. Sign in to the account whose node you want to control.");
        }

        return matches.SingleOrDefault();
    }

    /// <summary>Persists an enrolled device independently of the ordinary account token.</summary>
    public async Task SaveNodeAsync(CliNodeEntry node, CancellationToken ct)
    {
        var normalizedHost = CliHostResolver.NormalizeHostUrl(node.Host);
        var file = await ReadAsync(ct).ConfigureAwait(false) ?? new CliAuthFile();
        for (var index = file.Nodes.Count - 1; index >= 0; index--)
        {
            if (file.Nodes[index].UserId == node.UserId && HostMatches(file.Nodes[index].Host, normalizedHost))
            {
                file.Nodes.RemoveAt(index);
            }
        }

        file.Nodes.Add(node with { Host = normalizedHost });
        await WriteAsync(file, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string host, CancellationToken cancellationToken)
    {
        var normalizedHost = CliHostResolver.NormalizeHostUrl(host);
        var file = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (file is null) { return; }

        RemoveHosts(file.Hosts, normalizedHost);
        // Retained node enrollment keeps local control on the same host after logout.
        if (!string.IsNullOrWhiteSpace(file.CurrentHost) && HostMatches(file.CurrentHost, normalizedHost)
            && !file.Nodes.Any(node => HostMatches(node.Host, normalizedHost)))
        {
            file.CurrentHost = file.Hosts.FirstOrDefault()?.Host;
        }

        if (file.Hosts.Count == 0 && file.Nodes.Count == 0)
        {
            await DeleteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(paths.AuthFilePath))
        {
            File.Delete(paths.AuthFilePath);
        }

        return Task.CompletedTask;
    }

    private static bool HostMatches(string host, string normalizedHost)
    {
        try
        {
            return string.Equals(CliHostResolver.NormalizeHostUrl(host), normalizedHost, StringComparison.OrdinalIgnoreCase);
        }
        catch (CliExitException)
        {
            return false;
        }
    }

    private static void RemoveHosts(IList<CliAuthHostEntry> hosts, string normalizedHost)
    {
        for (var index = hosts.Count - 1; index >= 0; index--)
        {
            if (HostMatches(hosts[index].Host, normalizedHost))
            {
                hosts.RemoveAt(index);
            }
        }
    }
}
