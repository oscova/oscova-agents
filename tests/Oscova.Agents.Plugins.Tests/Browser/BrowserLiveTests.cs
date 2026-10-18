using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Browsing;
using Oscova.Agents.Plugins.Browser;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Tests.Browser;

/// <summary>Checks the real Chromium capability against a disposable local website.</summary>
public sealed class BrowserLiveTests
{
    [BrowserFact]
    [Trait("Category", "Slow")]
    public async Task Download_DirectoryReplacementCannotWriteOutsideGrant()
    {
        var root = Directory.CreateTempSubdirectory("oscova-browser-download-");
        var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(workspace, "current")).FullName;
        var parked = Path.Combine(workspace, "parked");
        var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside")).FullName;
        await using var site = LocalSite.Start(response =>
        {
            var file = response.Request.Url!.AbsolutePath == "/file";
            response.Response.ContentType = file ? "application/octet-stream" : "text/html";
            if (file) { response.Response.AddHeader("Content-Disposition", "attachment; filename=fixture.bin"); }
            return file ? new byte[32 * 1024 * 1024] : Encoding.UTF8.GetBytes("<a href='/file' download>Download fixture</a>");
        });
        try
        {
            TaskCompletionSource? resolving = null;
            var overlappingAlias = false;
            var session = new BrowserSessionScope("test", true);
            var context = CreateContext(root.FullName, session, () =>
            {
                resolving?.TrySetResult();
                return overlappingAlias
                    ? [new(workspace, "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/mnt/downloads"),
                       new(outside, "other", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, workspace)]
                    : [new(workspace, "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)];
            });
            await using var browser = new BrowserToolset(context);
            var page = (await browser.ExecuteAsync(session, new("open", Text: site.Origin))).Page!;
            var snapshot = (await browser.ExecuteAsync(session, new("read", page.Id))).Snapshot!;
            var reference = Regex.Match(snapshot, @"\[ref=(?<reference>[^\]]+)\]", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["reference"].Value;
            await browser.ExecuteAsync(session, new("click", page.Id, Reference: reference));
            BrowserDownload? download = null;
            for (var attempt = 0; attempt < 100 && download is null; attempt++)
            {
                download = (await browser.ExecuteAsync(session, new("downloads", page.Id))).Downloads?.SingleOrDefault(item => item.State == "completed");
                if (download is null) { await Task.Delay(50); }
            }
            Assert.NotNull(download);
            var ordinary = Path.Combine(workspace, "ordinary.bin");
            await browser.Downloads(page.Id, download.Id, ordinary);
            Assert.Equal(32 * 1024 * 1024, new FileInfo(ordinary).Length);
            await Assert.ThrowsAnyAsync<IOException>(() => browser.Downloads(page.Id, download.Id, ordinary));
            for (var attempt = 0; attempt < 20; attempt++)
            {
                resolving = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var signal = resolving;
                var replace = Task.Run(async () =>
                {
                    await signal.Task;
                    await Task.Delay(5);
                    Directory.Move(current, parked);
                    Directory.CreateSymbolicLink(current, outside);
                });
                var name = $"download-{attempt}.bin";
                try
                {
                    try { await browser.Downloads(page.Id, download.Id, Path.Combine(current, name)); }
                    catch (UnauthorizedAccessException) { }
                    await replace;
                    Assert.False(File.Exists(Path.Combine(outside, name)), "Download wrote outside the granted workspace.");
                }
                finally
                {
                    await replace;
                    Directory.Delete(current);
                    Directory.Move(parked, current);
                }
            }
            resolving = null;
            overlappingAlias = true;
            await browser.Downloads(page.Id, download.Id, "/mnt/downloads/aliased.bin");
            Assert.True(File.Exists(Path.Combine(workspace, "aliased.bin")), "Download re-resolved the host path into another granted alias.");
            Assert.False(File.Exists(Path.Combine(outside, "aliased.bin")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [BrowserFact]
    [Trait("Category", "Slow")]
    public async Task RegularProfile_KeepsSignInCookiesButNotVisitedPages()
    {
        var root = Directory.CreateTempSubdirectory("oscova-browser-profile-");
        var visited = $"visited-{Guid.NewGuid():N}";
        await using var site = LocalSite.Start(response =>
        {
            response.Response.ContentType = "text/html";
            response.Response.AddHeader("Set-Cookie", "oscova_sign_in=kept; Max-Age=3600; Path=/");
            response.Response.AddHeader("Cache-Control", "max-age=3600");
            return Encoding.UTF8.GetBytes($"<title>{visited}</title><p>Signed in</p>");
        });
        try
        {
            var session = new BrowserSessionScope("test", false);
            var context = CreateContext(root.FullName, session, () => []);
            await using (var browser = new BrowserToolset(context))
            {
                var page = (await browser.ExecuteAsync(session, new("open", Text: $"{site.Origin}{visited}?q={visited}"))).Page!;
                await browser.ExecuteAsync(session, new("read", page.Id));
                await browser.CloseSessionAsync(session.Id);
            }

            var profile = Path.Combine(root.FullName, "browser-profile");
            var files = Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).ToArray();
            Assert.Contains(files, file => Path.GetFileName(file) == "Cookies" && Contains(file, "oscova_sign_in"));
            var leaks = files.Where(file => Contains(file, visited)).Select(file => Path.GetRelativePath(profile, file)).ToArray();
            Assert.True(leaks.Length == 0, $"Visited page persisted in: {string.Join(", ", leaks)}");
            // Encrypted tab sessions hide the URL from the content scan above.
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(profile, "Default"), "Sessions*"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static AgentPluginContext CreateContext(string root, BrowserSessionScope session, Func<IReadOnlyList<AllowedDirectory>> directories)
    {
        var template = PluginTestContext.Create(new MissingCredentials(), root, root);
        return new AgentPluginContext(template.CredentialResolver, template.Services,
            NullLoggerFactory.Instance, TimeProvider.System,
            Environment.GetEnvironmentVariable("OSCOVA_BROWSER_PLUGIN_DIRECTORY")!, root)
        {
            GetAllowedDirectories = directories,
            UserDataDirectory = root,
            ResolveBrowserSessionAsync = _ => Task.FromResult(session)
        };
    }

    private static bool Contains(string file, string text)
    {
        var bytes = File.ReadAllBytes(file);
        return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0
            || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
    }

    /// <summary>Serves generated responses from a loopback listener until disposed.</summary>
    private sealed class LocalSite : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _serve = Task.CompletedTask;

        public string Origin { get; private init; } = string.Empty;

        public static LocalSite Start(Func<HttpListenerContext, byte[]> respond)
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var site = new LocalSite { Origin = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/" };
            port.Stop();
            site._listener.Prefixes.Add(site.Origin);
            site._listener.Start();
            site._serve = Task.Run(async () =>
            {
                try
                {
                    while (!site._stop.IsCancellationRequested)
                    {
                        var request = await site._listener.GetContextAsync().WaitAsync(site._stop.Token);
                        var bytes = respond(request);
                        request.Response.ContentLength64 = bytes.Length;
                        // Chromium may abandon a response it has already handed to its download manager.
                        try
                        {
                            await request.Response.OutputStream.WriteAsync(bytes, site._stop.Token);
                            request.Response.Close();
                        }
                        catch (HttpListenerException) when (!site._stop.IsCancellationRequested) { request.Response.Abort(); }
                    }
                }
                catch (OperationCanceledException) when (site._stop.IsCancellationRequested) { }
                catch (HttpListenerException) when (site._stop.IsCancellationRequested) { }
            });
            return site;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _serve;
            _listener.Close();
            _stop.Dispose();
        }
    }

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }

    private sealed class BrowserFactAttribute : FactAttribute
    {
        public BrowserFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OSCOVA_BROWSER_PLUGIN_DIRECTORY")))
            { Skip = "Set OSCOVA_BROWSER_PLUGIN_DIRECTORY to a root containing packages/microsoft.playwright/1.63.0 with matching Chromium installed."; }
        }
    }
}
