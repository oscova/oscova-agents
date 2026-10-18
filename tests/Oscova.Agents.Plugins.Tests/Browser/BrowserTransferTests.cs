using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Browsing;
using Oscova.Agents.Plugins.Browser;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Tests.Browser;

/// <summary>Checks the public upload tool's file boundary before host browser dispatch.</summary>
public sealed class BrowserTransferTests
{
    [Fact]
    public async Task Upload_DirectoryReplacementCannotReadOutsideGrant()
    {
        var root = Directory.CreateTempSubdirectory("oscova-browser-transfer-");
        var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(workspace, "current")).FullName;
        var parked = Path.Combine(workspace, "parked");
        var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(current, "data.txt"), new byte[65536]);
        await File.WriteAllBytesAsync(Path.Combine(outside, "data.txt"), Enumerable.Repeat((byte)9, 65536).ToArray());
        using var stop = new CancellationTokenSource();
        Task? replacements = null;
        try
        {
            var template = PluginTestContext.Create(new MissingCredentials(), root.FullName, root.FullName);
            var disclosed = false;
            var dispatched = 0;
            var context = new AgentPluginContext(template.CredentialResolver, template.Services,
                NullLoggerFactory.Instance, TimeProvider.System, root.FullName, root.FullName)
            {
                GetAllowedDirectories = () => [new(workspace, "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)],
                ResolveBrowserSessionAsync = _ => Task.FromResult(new BrowserSessionScope("test", true)),
                ExecuteBrowserCommandAsync = (command, _) =>
                {
                    if (command.Files is { } files)
                    {
                        disclosed |= files.Any(file => file.Data.Span[0] == 9);
                        dispatched++;
                    }
                    return Task.FromResult(new BrowserResult(Pages: []));
                }
            };
            await using var browser = new BrowserToolset(context);
            await browser.Upload("page", [Path.Combine(current, "data.txt")]);
            Assert.Equal(1, dispatched);
            replacements = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { Directory.Move(current, parked); }
                    catch (IOException) { continue; }
                    try
                    {
                        Directory.CreateSymbolicLink(current, outside);
                        Thread.Yield();
                    }
                    finally
                    {
                        Assert.True(SpinWait.SpinUntil(() =>
                        {
                            try
                            {
                                if (new DirectoryInfo(current).LinkTarget is not null) { Directory.Delete(current); }
                                Directory.Move(parked, current);
                                return true;
                            }
                            catch (IOException) { return false; }
                        }, TimeSpan.FromSeconds(10)), "The replacement fixture could not restore its directory.");
                    }
                }
            });
            for (var attempt = 0; attempt < 3000 && !disclosed; attempt++)
            {
                try { await browser.Upload("page", [Path.Combine(current, "data.txt")]); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Assert.False(disclosed, "Upload read the disposable file outside the granted workspace.");
        }
        finally
        {
            await stop.CancelAsync();
            if (replacements is not null) { await replacements; }
            root.Delete(recursive: true);
        }
    }

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }
}
