using Oscova.Agents.Plugins.Tests.TestSupport;
using Microsoft.Extensions.AI;
using System.Text;
using System.Net;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using NuGet.Packaging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Harness;
using Oscova.Agents.Plugins.GoogleWorkspace;
using Oscova.Agents.Policies;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.GoogleWorkspace;

/// <summary>Proves Google registration, disconnected failures and package ownership.</summary>
public sealed class GoogleWorkspaceTests
{
    /// <summary>Loads the real assembly and retains its provider and tool declarations.</summary>
    [Fact]
    public void Loader_PreservesAccountMetadataAndSharedFrameworkIdentity()
    {
        using var root = TestPluginRoot.Create();
        root.AddPluginFromProjectOutput(Path.Combine("src", "Oscova.Agents.Plugins.GoogleWorkspace"), "oscova.google-workspace");
        Assert.Empty(AgentPluginLoader.LoadDirectory(root.RootPath, new HashSet<string>()).Plugins);
        var catalog = AgentPluginLoader.LoadDirectory(root.RootPath);
        Assert.Empty(catalog.Diagnostics);
        var plugin = Assert.Single(catalog.Plugins);
        var provider = Assert.Single(plugin.OAuthProviders);
        Assert.Equal("google", provider.Id);
        Assert.Equal("Google", provider.DisplayName);
        Assert.Equal("Gmail, Calendar, YouTube", provider.Services);
        Assert.Equal(7, provider.Scopes.Count);
        Assert.Equal("offline", provider.AuthorizationParameters["access_type"]);
        Assert.Equal("consent", provider.AuthorizationParameters["prompt"]);
        Assert.Same(provider.AppCredential, Assert.Single(plugin.Credentials));
        Assert.Equal(CredentialScope.System, provider.AppCredential.Scope);
        Assert.Equal(new[] { "clientId", "clientSecret" }, provider.AppCredential.Fields.Select(field => field.Name));
        Assert.Same(typeof(OAuthProviderDefinition).Assembly, provider.GetType().Assembly);
        using var identity = JsonDocument.Parse("{\"email\":\"person@example.test\"}");
        Assert.Equal("person@example.test", provider.ReadConnectedEmail(identity.RootElement));
        Assert.Equal(3, plugin.Toolsets.Count);
        var tools = plugin.Toolsets.SelectMany(toolset => toolset.ToolsetType.GetMethods())
            .Where(method => method.GetCustomAttribute<DisplayNameAttribute>() is not null).ToArray();
        Assert.Equal(7, tools.Length);
        Assert.All(tools, method => Assert.NotNull(method.GetCustomAttribute<ToolNetworkScopeAttribute>()));
        Assert.DoesNotContain(tools, method => method.Name.Contains("Send", StringComparison.Ordinal));
    }

    /// <summary>Preserves actionable failures and propagates cancellation without remote requests.</summary>
    [Fact]
    public async Task Tools_RejectMissingTokensAndIdentifiersAndPropagateCancellation()
    {
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath());
        var gmail = new GmailToolset(context);
        var calendar = new GoogleCalendarToolset(context);
        var youtube = new YouTubeToolset(context);
        foreach (var read in new Func<Task<string>>[] { () => gmail.SearchEmailsAsync(), () => calendar.ListEventsAsync(), () => youtube.ListSubscriptionsAsync() })
        {
            var failure = await Assert.ThrowsAsync<ToolFailureException>(read);
            Assert.Contains("not connected", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Settings", failure.Message, StringComparison.Ordinal);
        }
        foreach (var read in new Func<Task<string>>[] { () => gmail.ReadEmailAsync(" "), () => calendar.GetEventAsync(" "), () => youtube.GetVideoDetailsAsync(" ") })
        {
            var failure = await Assert.ThrowsAsync<ToolFailureException>(read);
            Assert.Contains("ID is required", failure.Message, StringComparison.Ordinal);
        }
        var cancelled = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
            (_, _) => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => new GmailToolset(cancelled).SearchEmailsAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(() => new GoogleCalendarToolset(cancelled).ListEventsAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(() => new YouTubeToolset(cancelled).ListSubscriptionsAsync());
    }

    /// <summary>Checks the package carries Google dependencies without private or duplicate framework assemblies.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Package_ContainsManifestAndVendorAndFrameworkDependencies()
    {
        var directory = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? throw new InvalidOperationException("Set OSCOVA_NUGET_PACKAGE_DIRECTORY to the reviewed local feed.");
        using var package = new PackageArchiveReader(Path.Combine(directory, $"Oscova.Agents.Plugins.GoogleWorkspace.{PackageVersions.Get("Oscova.Agents.Plugins.GoogleWorkspace")}.nupkg"));
        var files = package.GetFiles().ToArray();
        Assert.Contains("oscova-plugin.json", files);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.GoogleWorkspace.dll", files);
        Assert.DoesNotContain(files, file => file.EndsWith("/Oscova.Agents.dll", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Core", StringComparison.Ordinal) || file.Contains("Oscova.Agents.Infrastructure", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Host", StringComparison.Ordinal));
        Assert.Equal(new[] { "Google.Apis.Calendar.v3", "Google.Apis.Gmail.v1", "Google.Apis.YouTube.v3", "Oscova.Agents" },
            package.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages).Select(dependency => dependency.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>Preserves invocation cancellation through token resolution and actual SDK requests.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task Reads_CancelAtTokenAndHttpBoundaries(int index, bool atHttp)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var source = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        using var cleanup = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var httpCalls = 0;
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
            async (_, ct) =>
            {
                Assert.Equal(source.Token, ct);
                if (atHttp) { return "access-token"; }
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Token resolution continued after cancellation.");
            },
            () => new StubHttpMessageHandler(async (_, ct) =>
            {
                Interlocked.Increment(ref httpCalls);
                entered.TrySetResult();
                using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, cleanup.Token);
                await Task.Delay(Timeout.Infinite, handlerCancellation.Token);
                throw new InvalidOperationException("HTTP continued after cancellation.");
            }));
        var (target, method, arguments, _) = CreateRead(index, context);
        var function = AIFunctionFactory.Create(target.GetType().GetMethod(method)!, target);
        Assert.False(function.JsonSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("ct", out _));
        var invocation = function.InvokeAsync(arguments, source.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            await source.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(invocation.IsCompleted);
            Assert.False(budget.IsCancellationRequested);
            Assert.Equal(atHttp ? 1 : 0, httpCalls);
        }
        finally
        {
            await cleanup.CancelAsync();
            await source.CancelAsync();
            try { await invocation.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Retains safe provider failures through actual tool and SDK execution.</summary>
    [Theory]
    [InlineData(0, 401)]
    [InlineData(0, 403)]
    [InlineData(0, 400)]
    [InlineData(2, 401)]
    [InlineData(2, 403)]
    [InlineData(2, 400)]
    [InlineData(4, 401)]
    [InlineData(4, 403)]
    [InlineData(4, 400)]
    public async Task Reads_PreserveProviderFailureMessages(int index, int status)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var json = JsonSerializer.Serialize(new { error = new { code = status, message = "denied",
            errors = new[] { new { message = "denied", domain = "global", reason = "authError" } } } });
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
            (_, _) => Task.FromResult<string?>("access-token"),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var (target, method, arguments, service) = CreateRead(index, context);
        var function = AIFunctionFactory.Create(target.GetType().GetMethod(method)!, target);
        var failure = await Assert.ThrowsAsync<ToolFailureException>(() =>
            function.InvokeAsync(arguments, budget.Token).AsTask().WaitAsync(budget.Token));
        Assert.Equal(status is 401 or 403
            ? $"{service} is not connected. Ask the user to connect their Google account."
            : $"The {service} request failed.", failure.Message);
        Assert.Equal(status is 401 or 403 ? null : "Check the connection or try again later.", failure.Fix);
    }

    /// <summary>Reads a calendar range without an offset in the user's time zone.</summary>
    [Fact]
    public async Task ListEvents_WhenRangeHasNoOffset_UsesTheUserTimeZone()
    {
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
            (_, _) => Task.FromResult<string?>("access-token"),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"items\":[{\"id\":\"e1\",\"summary\":\"Standup\",\"start\":{\"dateTime\":\"2026-10-17T09:30:00+14:00\"},\"end\":{\"dateTime\":\"2026-10-17T10:00:00+14:00\"}}]}", Encoding.UTF8, "application/json") }),
            clock: new ZoneClock(TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati")));

        var result = await new GoogleCalendarToolset(context).ListEventsAsync("2026-10-17T09:00:00", "2026-10-17T17:00:00");

        Assert.Contains("Range: 2026-10-17 09:00 -> 2026-10-17 17:00", result, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ToolFailureException>(() => new GoogleCalendarToolset(context).ListEventsAsync("next friday"));
    }

    private sealed class ZoneClock(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static (object Target, string Method, AIFunctionArguments Arguments, string Service) CreateRead(
        int index, AgentPluginContext context) => index switch
    {
        0 => (new GmailToolset(context), nameof(GmailToolset.SearchEmailsAsync), new(), "Gmail"),
        1 => (new GmailToolset(context), nameof(GmailToolset.ReadEmailAsync), new() { ["messageId"] = "message-1" }, "Gmail"),
        2 => (new GoogleCalendarToolset(context), nameof(GoogleCalendarToolset.ListEventsAsync), new(), "Google Calendar"),
        3 => (new GoogleCalendarToolset(context), nameof(GoogleCalendarToolset.GetEventAsync), new() { ["eventId"] = "event-1" }, "Google Calendar"),
        4 => (new YouTubeToolset(context), nameof(YouTubeToolset.ListSubscriptionsAsync), new(), "YouTube"),
        5 => (new YouTubeToolset(context), nameof(YouTubeToolset.CheckNewUploadsAsync), new(), "YouTube"),
        6 => (new YouTubeToolset(context), nameof(YouTubeToolset.GetVideoDetailsAsync), new() { ["videoId"] = "video-1" }, "YouTube"),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }
}
