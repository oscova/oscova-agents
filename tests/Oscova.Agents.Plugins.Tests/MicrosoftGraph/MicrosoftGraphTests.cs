using Oscova.Agents.Plugins.Tests.TestSupport;
using System.Text;
using System.Net;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NuGet.Packaging;
using Oscova.Agents.Collections;
using Oscova.Agents.Credentials;
using Oscova.Agents.Harness;
using Oscova.Agents.Plugins.MicrosoftGraph;
using Oscova.Agents.Policies;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.MicrosoftGraph;

/// <summary>Checks Microsoft account metadata, tool guards and Graph request authentication.</summary>
public sealed class MicrosoftGraphTests
{
    /// <summary>Releases each operation's transport after successful and failed SDK requests.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Operations_DisposeOwnedTransport(int index)
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest })
        {
            using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
            { Content = new StringContent("{\"id\":\"item\",\"value\":[]}", Encoding.UTF8, "application/json") });
            var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
                (_, _) => Task.FromResult<string?>("access-token"), () => handler);
            var outlook = new OutlookToolset(context);
            var todo = new MicrosoftTodoToolset(context);
            Func<Task<string>> operation = index switch
            {
                0 => () => outlook.ListEmailsAsync(),
                1 => () => outlook.ReadEmailAsync("message-1"),
                2 => () => outlook.SearchEmailsAsync("subject:test"),
                3 => () => outlook.SendEmailAsync("recipient@example.test", "Subject", "Body"),
                4 => () => todo.ListTaskListsAsync(),
                5 => () => todo.ListTasksAsync("list-1"),
                6 => () => todo.CreateTaskAsync("list-1", "Task"),
                7 => () => todo.UpdateTaskAsync("list-1", "task-1", title: "Updated"),
                8 => () => todo.DeleteTaskAsync("list-1", "task-1", confirmDelete: true),
                _ => throw new ArgumentOutOfRangeException(nameof(index))
            };
            if (status == HttpStatusCode.OK) { await operation(); }
            else { await Assert.ThrowsAsync<ToolFailureException>(operation); }
            Assert.True(handler.IsDisposed);
        }
    }

    /// <summary>Loads the real package declaration and preserves Microsoft account and tool metadata.</summary>
    [Fact]
    public void Loader_PreservesAccountMetadataAndSharedFrameworkIdentity()
    {
        using var root = TestPluginRoot.Create();
        root.AddPluginFromProjectOutput(Path.Combine("src", "Oscova.Agents.Plugins.MicrosoftGraph"), "oscova.microsoft-graph");
        Assert.Empty(AgentPluginLoader.LoadDirectory(root.RootPath, new HashSet<string>()).Plugins);
        var catalog = AgentPluginLoader.LoadDirectory(root.RootPath);
        Assert.Empty(catalog.Diagnostics);
        var plugin = Assert.Single(catalog.Plugins);
        var provider = Assert.Single(plugin.OAuthProviders);
        Assert.Equal("microsoft", provider.Id);
        Assert.Equal("Microsoft", provider.DisplayName);
        Assert.Equal("Outlook, To-Do, Microsoft Graph", provider.Services);
        Assert.Equal("Outlook, To-Do, Graph integrations", provider.Description);
        Assert.Equal("oauth-provider:microsoft", provider.AppCredential.Key);
        Assert.Equal(CredentialScope.System, provider.AppCredential.Scope);
        Assert.Equal(new[] { "clientId", "clientSecret" }, provider.AppCredential.Fields.Select(field => field.Name));
        Assert.False(provider.AppCredential.Fields[0].IsSecret);
        Assert.True(provider.AppCredential.Fields[1].IsSecret);
        Assert.All(provider.AppCredential.Fields, field => Assert.True(field.IsRequired));
        Assert.Equal("login.microsoftonline.com", provider.AuthorizationEndpoint.Host);
        Assert.Equal("login.microsoftonline.com", provider.TokenEndpoint.Host);
        Assert.Equal("https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName", provider.UserInfoEndpoint.AbsoluteUri);
        Assert.Equal(8, provider.Scopes.Count);
        Assert.Contains("https://graph.microsoft.com/Mail.Send", provider.Scopes);
        Assert.Contains("https://graph.microsoft.com/Tasks.ReadWrite", provider.Scopes);
        Assert.Same(provider.AppCredential, Assert.Single(plugin.Credentials));
        Assert.Same(typeof(OAuthProviderDefinition).Assembly, provider.GetType().Assembly);
        using var mail = JsonDocument.Parse("{\"mail\":\"mail@example.test\",\"userPrincipalName\":\"upn@example.test\"}");
        using var fallback = JsonDocument.Parse("{\"mail\":null,\"userPrincipalName\":\"upn@example.test\"}");
        Assert.Equal("mail@example.test", provider.ReadConnectedEmail(mail.RootElement));
        Assert.Equal("upn@example.test", provider.ReadConnectedEmail(fallback.RootElement));
        Assert.Equal(2, plugin.Toolsets.Count);
        var methods = plugin.Toolsets.SelectMany(toolset => toolset.ToolsetType.GetMethods())
            .Where(method => method.GetCustomAttribute<DisplayNameAttribute>() is not null).ToArray();
        Assert.Equal(9, methods.Length);
        Assert.All(methods, method => Assert.NotNull(method.GetCustomAttribute<ToolNetworkScopeAttribute>()));
        Assert.All(methods, method => Assert.Equal(NetworkScope.Egress, method.GetCustomAttribute<ToolNetworkScopeAttribute>()!.Scope));
        var context = CreateContext();
        var tools = new AgentToolCollection();
        tools.Add(new OutlookToolset(context), plugin.Manifest.Id);
        tools.Add(new MicrosoftTodoToolset(context), plugin.Manifest.Id);
        Assert.Equal(ToolApprovalMode.RequireApproval, tools.GetDefaultApprovalMode(
            Assert.Single(tools, tool => tool.Name.EndsWith("__send_email", StringComparison.Ordinal)).Name));
        Assert.Equal(ToolApprovalMode.RequireApproval, tools.GetDefaultApprovalMode(
            Assert.Single(tools, tool => tool.Name.EndsWith("__delete", StringComparison.Ordinal)).Name));
    }

    /// <summary>Rejects missing tokens and identifiers before sending a Graph request.</summary>
    [Fact]
    public async Task Tools_RejectMissingTokensAndIdentifiers()
    {
        var context = CreateContext();
        var outlook = new OutlookToolset(context);
        var todo = new MicrosoftTodoToolset(context);
        foreach (var read in new Func<Task<string>>[] { () => outlook.ListEmailsAsync(), () => todo.ListTaskListsAsync() })
        {
            var failure = await Assert.ThrowsAsync<ToolFailureException>(read);
            Assert.Contains("not connected", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Settings", failure.Message, StringComparison.Ordinal);
        }
        foreach (var read in new Func<Task<string>>[] { () => outlook.ReadEmailAsync(" "), () => todo.ListTasksAsync(" ") })
        {
            var failure = await Assert.ThrowsAsync<ToolFailureException>(read);
            Assert.Contains("ID is required", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Checks the archive contains the plugin assembly and Graph and framework dependencies.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Package_ContainsManifestAndVendorAndFrameworkDependencies()
    {
        var directory = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? throw new InvalidOperationException("Set OSCOVA_NUGET_PACKAGE_DIRECTORY to the reviewed local feed.");
        using var package = new PackageArchiveReader(Path.Combine(directory, $"Oscova.Agents.Plugins.MicrosoftGraph.{PackageVersions.Get("Oscova.Agents.Plugins.MicrosoftGraph")}.nupkg"));
        var files = package.GetFiles().ToArray();
        Assert.Contains("oscova-plugin.json", files);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.MicrosoftGraph.dll", files);
        Assert.DoesNotContain(files, file => file.EndsWith("/Oscova.Agents.dll", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Core", StringComparison.Ordinal) || file.Contains("Oscova.Agents.Infrastructure", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Host", StringComparison.Ordinal));
        Assert.Equal(new[] { "Microsoft.Graph", "Microsoft.Kiota.Abstractions", "Oscova.Agents" },
            package.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages).Select(dependency => dependency.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>Propagates cancellation into each mutation's second token lookup before network IO.</summary>
    [Theory]
    [InlineData("outlook__send_email")]
    [InlineData("microsofttodo__create")]
    [InlineData("microsofttodo__update")]
    [InlineData("microsofttodo__delete")]
    public async Task MutatingTools_WhenCancelledDuringGraphAuthentication_PropagateCancellation(string toolName)
    {
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<CancellationToken>();
        var context = CreateContext((provider, ct) =>
        {
            Assert.Equal("microsoft", provider);
            seen.Add(ct);
            if (seen.Count == 1) { return Task.FromResult<string?>("access-token"); }
            entered.TrySetResult();
            return WaitForCancellationAsync(ct);
        });
        var outlook = new OutlookToolset(context);
        var todo = new MicrosoftTodoToolset(context);
        var (target, methodName, arguments) = toolName switch
        {
            "outlook__send_email" => ((object)outlook, nameof(OutlookToolset.SendEmailAsync), new AIFunctionArguments
            {
                ["to"] = "recipient@example.com", ["subject"] = "Subject", ["body"] = "Body"
            }),
            "microsofttodo__create" => ((object)todo, nameof(MicrosoftTodoToolset.CreateTaskAsync), new AIFunctionArguments
            {
                ["taskListId"] = "list-1", ["title"] = "Task"
            }),
            "microsofttodo__update" => ((object)todo, nameof(MicrosoftTodoToolset.UpdateTaskAsync), new AIFunctionArguments
            {
                ["taskListId"] = "list-1", ["taskId"] = "task-1", ["title"] = "Updated task"
            }),
            "microsofttodo__delete" => ((object)todo, nameof(MicrosoftTodoToolset.DeleteTaskAsync), new AIFunctionArguments
            {
                ["taskListId"] = "list-1", ["taskId"] = "task-1", ["confirmDelete"] = true
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown Microsoft tool.")
        };
        var method = target.GetType().GetMethod(methodName)!;
        var invocation = AIFunctionFactory.Create(method, target).InvokeAsync(arguments, source.Token).AsTask();
        await entered.Task.WaitAsync(source.Token);
        Assert.Equal([source.Token, source.Token], seen);
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static AgentPluginContext CreateContext(
        Func<string, CancellationToken, Task<string?>>? resolve = null) =>
        PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(), resolve);

    private static async Task<string?> WaitForCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        throw new InvalidOperationException("The Graph request continued after cancellation.");
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
    [InlineData(3, 401)]
    [InlineData(3, 403)]
    [InlineData(3, 400)]
    public async Task Reads_PreserveProviderFailureMessages(int index, int status)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var json = JsonSerializer.Serialize(new { error = new { code = "ErrorAccessDenied", message = "denied" } });
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
            (_, _) => Task.FromResult<string?>("access-token"),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var (target, method, arguments, service) = CreateRead(index, context);
        var function = AIFunctionFactory.Create(target.GetType().GetMethod(method)!, target);
        var failure = await Assert.ThrowsAsync<ToolFailureException>(() =>
            function.InvokeAsync(arguments, budget.Token).AsTask().WaitAsync(budget.Token));
        Assert.Equal(status is 401 or 403
            ? $"{service} is not connected. Ask the user to connect their Microsoft account through the host connected accounts setup."
            : $"The {service} request failed.", failure.Message);
        Assert.Equal(status is 401 or 403 ? null : "Check the connection or try again later.", failure.Fix);
    }

    /// <summary>Sends due dates as the user's wall clock so To Do keeps the intended day.</summary>
    [Theory]
    [InlineData("2026-10-20")]
    [InlineData("2026-10-19T10:00:00Z")]
    public async Task CreateTask_SendsDueDateInUserTimeZone(string dueDateTime)
    {
        JsonElement? sentDue = null;
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath(),
            (_, _) => Task.FromResult<string?>("access-token"),
            () => new StubHttpMessageHandler(async (request, ct) =>
            {
                using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                sentDue = sent.RootElement.GetProperty("dueDateTime").Clone();
                return new HttpResponseMessage(HttpStatusCode.Created)
                { Content = new StringContent("{\"id\":\"task-1\",\"title\":\"Task\"}", Encoding.UTF8, "application/json") };
            }),
            clock: new ZoneClock(TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati")));

        await new MicrosoftTodoToolset(context).CreateTaskAsync("list-1", "Task", dueDateTime: dueDateTime);

        Assert.NotNull(sentDue);
        Assert.Equal("2026-10-20T00:00:00", sentDue.Value.GetProperty("dateTime").GetString());
        Assert.Equal("Line Islands Standard Time", sentDue.Value.GetProperty("timeZone").GetString());
    }

    private static (object Target, string Method, AIFunctionArguments Arguments, string Service) CreateRead(
        int index, AgentPluginContext context) => index switch
    {
        0 => (new OutlookToolset(context), nameof(OutlookToolset.ListEmailsAsync), new(), "Outlook"),
        1 => (new OutlookToolset(context), nameof(OutlookToolset.ReadEmailAsync), new() { ["messageId"] = "message-1" }, "Outlook"),
        2 => (new OutlookToolset(context), nameof(OutlookToolset.SearchEmailsAsync), new() { ["query"] = "subject:test" }, "Outlook"),
        3 => (new MicrosoftTodoToolset(context), nameof(MicrosoftTodoToolset.ListTaskListsAsync), new(), "Microsoft To Do"),
        4 => (new MicrosoftTodoToolset(context), nameof(MicrosoftTodoToolset.ListTasksAsync), new() { ["taskListId"] = "list-1" }, "Microsoft To Do"),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private sealed class ZoneClock(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }
}
