using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NuGet.Packaging;
using Oscova.Agents.Channels;
using Oscova.Agents.Plugins.Slack;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using SlackNet;
using SlackNet.Events;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.Slack;

/// <summary>Checks Slack declarations, protocol boundaries and transport ownership.</summary>
public sealed class SlackChannelTests
{
    /// <summary>Rejects wrong owners, bot messages, and duplicate Slack event deliveries.</summary>
    [Fact]
    public void EventGuard_AcceptsOnlyAuthorizedOwnerMessageOnce()
    {
        var guard = new Guard();
        var accepted = OwnerEvent();
        Assert.True(guard.TryAccept(accepted, "A_APP", "U_BOT", out var message));
        Assert.Equal("hello", message.Text);
        Assert.False(guard.TryAccept(accepted, "A_APP", "U_BOT", out _));
        var wrongTeam = OwnerEvent();
        wrongTeam.TeamId = "T_OTHER";
        Assert.False(guard.TryAccept(wrongTeam, "A_APP", "U_BOT", out _));
        var wrongApp = OwnerEvent();
        wrongApp.ApiAppId = "A_OTHER";
        Assert.False(guard.TryAccept(wrongApp, "A_APP", "U_BOT", out _));
        var shared = OwnerEvent();
        shared.IsExtSharedChannel = true;
        Assert.False(guard.TryAccept(shared, "A_APP", "U_BOT", out _));
        Assert.False(guard.TryAccept(OwnerEvent(user: "U_OTHER"), "A_APP", "U_BOT", out _));
        Assert.False(guard.TryAccept(OwnerEvent(channelType: "channel"), "A_APP", "U_BOT", out _));
        Assert.False(guard.TryAccept(OwnerEvent(subtype: "message_changed"), "A_APP", "U_BOT", out _));
        Assert.False(guard.TryAccept(OwnerEvent(botId: "B_BOT"), "A_APP", "U_BOT", out _));
    }

    [Fact]
    public void EventGuard_DoesNotEvictRetainedDuplicateAtCapacity()
    {
        var guard = new Guard();
        var first = OwnerEvent();
        Assert.True(guard.TryAccept(first, "A_APP", "U_BOT", out _));
        for (var index = 1; index < 1024; index++)
        {
            Assert.True(guard.TryAccept(OwnerEvent(), "A_APP", "U_BOT", out _));
        }

        Assert.False(guard.TryAccept(first, "A_APP", "U_BOT", out _));
        Assert.True(guard.TryAccept(OwnerEvent(), "A_APP", "U_BOT", out _));
        Assert.True(guard.TryAccept(first, "A_APP", "U_BOT", out _));
    }

    [Fact]
    public void EventGuard_DetectsMismatchedBotAndAppTokens()
    {
        var guard = new Guard();
        var mismatch = OwnerEvent();
        mismatch.Authorizations = [new SlackNet.Authorization
        {
            IsBot = true, TeamId = "T_OWNER", UserId = "U_OTHER_BOT"
        }];

        Assert.True(guard.HasMismatchedAuthorization(mismatch, "A_APP", "U_BOT"));
        Assert.False(guard.TryAccept(mismatch, "A_APP", "U_BOT", out _));
        Assert.False(guard.HasMismatchedAuthorization(OwnerEvent(), "A_APP", "U_BOT"));
    }

    private static EventCallback OwnerEvent(string user = "U_OWNER", string channelType = "im",
        string? subtype = null, string? botId = null) => new()
    {
        TeamId = "T_OWNER", ApiAppId = "A_APP", EventId = Guid.NewGuid().ToString("N"),
        Authorizations = [new SlackNet.Authorization { IsBot = true, TeamId = "T_OWNER", UserId = "U_BOT" }],
        Event = new MessageEvent
        {
            User = user, ChannelType = channelType, Subtype = subtype, BotId = botId,
            Channel = "D_OWNER", Ts = "123.456", Text = "hello"
        }
    };

    /// <summary>Sends bounded Slack messages without splitting escapes or surrogate pairs.</summary>
    [Fact]
    public async Task SendAsync_EscapesAndSplitsWholeTextThroughSlackApi()
    {
        var chunks = new List<string>();
        using var client = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("slack.com", request.RequestUri?.Host);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            chunks.Add(body.RootElement.GetProperty("text").GetString()!);
            Assert.Equal("123.456", body.RootElement.GetProperty("thread_ts").GetString());
            Assert.False(body.RootElement.GetProperty("unfurl_links").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { ok = true, channel = "D_OWNER", ts = "123.1" })
            };
        }));
        var builder = new SlackServiceBuilder()
            .UseApiToken("xoxb-test")
            .UseHttp(sp => Default.Http(sp.GetJsonSettings(), () => client, sp.GetLogger()));
        using var sender = (IDisposable)Activator.CreateInstance(PluginType("SlackReplySender"),
            builder.GetApiClient(), CancellationToken.None)!;
        var original = new string('x', 3994) + "&<>😀";

        await (Task)Invoke(sender, "SendAsync", "D_OWNER", "123.456", original, CancellationToken.None)!;
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(chunk.Length, 1, 4000);
            Assert.DoesNotContain("&", chunk.Replace("&amp;", "", StringComparison.Ordinal)
                .Replace("&lt;", "", StringComparison.Ordinal)
                .Replace("&gt;", "", StringComparison.Ordinal));
            Assert.False(char.IsHighSurrogate(chunk[^1]));
            Assert.False(char.IsLowSurrogate(chunk[0]));
        });
        Assert.Equal(original, WebUtility.HtmlDecode(string.Concat(chunks)));
    }


    /// <summary>Loads inert declarations and validates both fields without imposing an ID format.</summary>
    [Fact]
    public async Task LoadedPlugin_DeclaresTwoSecretsAndTwoFields()
    {
        using var root = TestPluginRoot.Create();
        root.AddPluginFromProjectOutput(Path.Combine("src", "Oscova.Agents.Plugins.Slack"), "oscova.slack");
        Assert.Empty(AgentPluginLoader.LoadDirectory(root.RootPath, new HashSet<string>()).Plugins);
        var catalog = AgentPluginLoader.LoadDirectory(root.RootPath);
        Assert.Empty(catalog.Diagnostics);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(["slack-bot"], plugin.DeclaredCredentialKeys);
        var channel = Assert.Single(plugin.Channels);
        Assert.Single(plugin.SettingsPanels);
        Assert.True(channel.Definition.SupportsNotifications);
        Assert.All(Assert.Single(plugin.Credentials).Fields, field => { Assert.True(field.IsSecret); Assert.True(field.IsRequired); });
        Assert.Empty(new SlackSettings().Validate(false));
        Assert.Equal(2, new SlackSettings().Validate(true).Count);
        Assert.Empty(Settings().Validate(true));
        var services = PluginTestContext.Create(new Credentials(null), Path.GetTempPath(), Path.GetTempPath(), settings: PluginTestContext.Settings(Settings()));
        var snapshot = await channel.Channel.ReadConfigurationAsync(services, true);
        await using var connection = snapshot.CreateConnection(Context());
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.StartAsync(CancellationToken.None));
    }

    /// <summary>Sends task notifications as an unthreaded direct message to the authorized member.</summary>
    [Fact]
    public async Task Notification_PostsDirectMessageToAuthorizedMember()
    {
        var posts = new List<JsonElement>();
        using var api = new HttpClient(new Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            posts.Add(body.RootElement.Clone());
            return Json(new { ok = true, channel = "D_OWNER", ts = "123.1" });
        }));
        var builder = new SlackServiceBuilder()
            .UseApiToken("xoxb-test")
            .UseHttp(sp => Default.Http(sp.GetJsonSettings(), () => api, sp.GetLogger()));
        await using var connection = await CreateConnection();
        Set(connection, "_sender", Activator.CreateInstance(PluginType("SlackReplySender"), builder.GetApiClient(), CancellationToken.None)!);
        await connection.SendNotificationAsync("Task ready", CancellationToken.None);
        var post = Assert.Single(posts);
        Assert.Equal("U_OWNER", post.GetProperty("channel").GetString());
        Assert.Equal("Task ready", post.GetProperty("text").GetString());
        Assert.False(post.TryGetProperty("thread_ts", out var thread) && thread.ValueKind != JsonValueKind.Null);
    }

    /// <summary>Checks the package boundary and exact vendor dependency.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Package_ContainsSlackAndNoPrivateAssemblies()
    {
        var directory = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? throw new InvalidOperationException("Set OSCOVA_NUGET_PACKAGE_DIRECTORY to the reviewed local feed.");
        using var package = new PackageArchiveReader(Path.Combine(directory, $"Oscova.Agents.Plugins.Slack.{PackageVersions.Get("Oscova.Agents.Plugins.Slack")}.nupkg"));
        var files = package.GetFiles().ToArray();
        Assert.Contains("oscova-plugin.json", files);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.Slack.dll", files);
        Assert.DoesNotContain(files, file => file.Contains("Oscova.Agents.Core", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Infrastructure", StringComparison.Ordinal) || file.Contains("Oscova.Agents.Host", StringComparison.Ordinal));
        var dependencies = package.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages).ToArray();
        Assert.Equal(new[] { "SlackNet", "Oscova.Agents" }.Order(StringComparer.Ordinal),
            dependencies.Select(dependency => dependency.Id).Order(StringComparer.Ordinal));
        var dependency = Assert.Single(dependencies, dependency => dependency.Id == "SlackNet");
        Assert.Equal("SlackNet", dependency.Id);
        Assert.Equal("0.18.0", dependency.VersionRange.MinVersion!.ToNormalizedString());
    }

    /// <summary>Disposes the connection-owned transport after failed authentication.</summary>
    [Fact]
    public async Task FailedStart_DisposesConnectionTransport()
    {
        for (var index = 0; index < 2; index++)
        {
            using var api = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { ok = false, error = "invalid_auth" }))));
            await using var connection = await CreateConnection(api);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.StartAsync(CancellationToken.None));
            Assert.Contains("rejected", error.Message, StringComparison.Ordinal);
            await connection.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => api.GetAsync("https://slack.com/api/auth.test"));
        }
    }

    /// <summary>Defers attachment IO and rejects unsafe hosts and redirects through the download callback.</summary>
    [Fact]
    public async Task Attachments_AreInertAndUseRedirectDisabledBearerRequests()
    {
        ChannelMessage? received = null;
        await using var connection = await CreateConnection(receive: message => received = message);
        var files = (HttpClient)Get(connection, "_files")!;
        files.Dispose();
        var calls = new List<(Uri Uri, AuthenticationHeaderValue? Authorization)>();
        var recording = new HttpClient(new Handler((request, _) =>
        {
            calls.Add((request.RequestUri!, request.Headers.Authorization));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://example.com/steal") }
            });
        }));
        recording.DefaultRequestHeaders.Authorization = new("Bearer", "secret");
        Set(connection, "_files", recording);
        Ready(connection);
        var callback = OwnerEvent();
        var message = (MessageEvent)callback.Event;
        message.ThreadTs = "thread.123";
        message.Files = new[] { "https://example.com/file", "http://files.slack.com/file",
            "https://files.slack.com:8443/file", "https://files.slack.com.evil.test/file",
            "https://user@files.slack.com/file", "https://files.slack.com/file" }
            .Select(url => new SlackNet.File { Name = "notes.txt", Size = 0, UrlPrivateDownload = url }).ToList();
        await (Task)Invoke(connection, "HandleAsync", callback)!;
        Assert.Empty(calls);
        Assert.Equal("thread.123", received!.ThreadId);
        Assert.False(received.IsBot);
        var attachment = Assert.Single(received.Attachments);
        Assert.Equal(0, attachment.Size);
        await Assert.ThrowsAsync<IOException>(() => attachment.OpenReadAsync(CancellationToken.None));
        Assert.Single(calls);
        Assert.Equal("files.slack.com", calls[0].Uri.Host);
        Assert.Equal("Bearer", calls[0].Authorization?.Scheme);
        Assert.Equal("secret", calls[0].Authorization?.Parameter);
    }

    /// <summary>Preserves terminal mismatch and disabled states despite later socket callbacks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalState_DoesNotReadmitEventsOrPublishConnected(bool mismatch)
    {
        var statuses = new List<ChannelConnectionStatus>();
        var messages = new List<ChannelMessage>();
        await using var connection = await CreateConnection(receive: messages.Add, status: statuses.Add);
        Ready(connection);
        using var current = Default.WebSocketFactory.Create("ws://127.0.0.1/current");
        using var stale = Default.WebSocketFactory.Create("ws://127.0.0.1/stale");
        Set(connection, "_currentSocket", current);
        await (Task)Invoke(connection, "OnSocketClosedAsync", stale)!;
        Assert.Empty(statuses);
        await (Task)Invoke(connection, "OnSocketClosedAsync", current)!;
        Assert.Equal(ChannelConnectionState.Disconnected, Assert.Single(statuses).State);
        statuses.Clear();
        if (mismatch)
        {
            var callback = OwnerEvent();
            callback.Authorizations[0].UserId = "different-bot";
            await (Task)Invoke(connection, "HandleAsync", callback)!;
        }
        else
        {
            Invoke(connection, "OnSocketMessage", new SlackNet.SocketMode.Disconnect
            { Reason = SlackNet.SocketMode.DisconnectReason.SocketModeDisabled });
        }
        Invoke(connection, "OnSocketMessage", Newtonsoft.Json.JsonConvert.DeserializeObject<SlackNet.SocketMode.Hello>(
            "{\"connection_info\":{\"app_id\":\"A_APP\"}}", Default.JsonSettings().SerializerSettings)!);
        await (Task)Invoke(connection, "HandleAsync", OwnerEvent())!;
        Assert.Empty(messages);
        Assert.Equal(ChannelConnectionState.Error, Assert.Single(statuses).State);
        await connection.StopAsync(CancellationToken.None);
        await connection.DisposeAsync();
        await (Task)Invoke(connection, "OnSocketClosedAsync", current)!;
        await (Task)Invoke(connection, "HandleAsync", OwnerEvent())!;
        Assert.Single(statuses);
        Assert.Empty(messages);
    }

    /// <summary>Races real starts against one local socket endpoint and retains the winner's reservation.</summary>
    [Fact]
    public async Task ConcurrentStarts_ShareClaimAndLoserCannotReleaseWinner()
    {
        await using var server = new SocketServer();
        var arrived = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct) => RespondCore(request, ct);
        async Task<HttpResponseMessage> RespondCore(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("auth.test", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrived) == 2) { barrier.TrySetResult(); }
                await barrier.Task.WaitAsync(ct);
                return Json(new { ok = true, team_id = "T_OWNER", user_id = "U_BOT", user = "bot" });
            }
            return Json(new { ok = true, url = server.Url });
        }
        var builder = new AgentPluginBuilder();
        new SlackAgentPlugin().Configure(builder);
        var contribution = Assert.Single(builder.Channels);
        async Task<IChannelConnection> Create()
        {
            var snapshot = await contribution.Channel.ReadConfigurationAsync(Services(), true);
            return snapshot.CreateConnection(Context(transport: new HttpClient(new Handler(Respond))));
        }
        await using var first = await Create();
        await using var second = await Create();
        var starts = new[] { first.StartAsync(CancellationToken.None), second.StartAsync(CancellationToken.None) };
        try { await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException) { }
        _ = Assert.Single(starts, task => task.IsCompletedSuccessfully);
        _ = Assert.Single(starts, task => task.IsFaulted);
        var winner = starts[0].IsCompletedSuccessfully ? first : second;
        var loser = ReferenceEquals(winner, first) ? second : first;
        await loser.DisposeAsync();
        await using var third = await Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => third.StartAsync(CancellationToken.None));
        await winner.StopAsync(CancellationToken.None);
        await using var fourth = await Create();
        await fourth.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static SlackSettings Settings() => new() { WorkspaceId = "T_OWNER", AuthorizedUserId = "U_OWNER" };
    private static ChannelConnectionContext Context(Action<ChannelMessage>? receive = null,
        Action<ChannelConnectionStatus>? status = null, HttpClient? transport = null) =>
        PluginTestContext.Channel( (message, _) => { receive?.Invoke(message); return Task.CompletedTask; },
            value => { status?.Invoke(value); return Task.CompletedTask; }, transport);
    private static AgentPluginContext Services() => PluginTestContext.Create(new Credentials("secret"),
        Path.GetTempPath(), Path.GetTempPath(), settings: PluginTestContext.Settings(Settings()));
    private static async Task<IChannelConnection> CreateConnection(HttpClient? api = null, Action<ChannelMessage>? receive = null,
        Action<ChannelConnectionStatus>? status = null)
    {
        var builder = new AgentPluginBuilder();
        new SlackAgentPlugin().Configure(builder);
        var services = api is null ? PluginTestContext.Create(new Credentials(null), Path.GetTempPath(), Path.GetTempPath(), settings: PluginTestContext.Settings(Settings())) : Services();
        var snapshot = await Assert.Single(builder.Channels).Channel.ReadConfigurationAsync(services, true);
        return snapshot.CreateConnection(Context(receive, status, api));
    }
    private static Type PluginType(string name) => typeof(SlackAgentPlugin).Assembly.GetType("Oscova.Agents.Plugins.Slack." + name)!;
    private static object? Invoke(object target, string method, params object?[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static object? Get(object target, string field) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Ready(IChannelConnection connection)
    {
        Set(connection, "_ready", true);
        Set(connection, "_appId", "A_APP");
        Set(connection, "_botUserId", "U_BOT");
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Credentials(string? value) : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult(value);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => Task.FromResult(value);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class Guard
    {
        private readonly object _guard = Activator.CreateInstance(PluginType("SlackEventGuard"), "T_OWNER", "U_OWNER")!;
        public bool TryAccept(EventCallback callback, string app, string bot, out MessageEvent message)
        {
            object?[] args = [callback, app, bot, null];
            var result = (bool)Invoke(_guard, "TryAccept", args)!;
            message = (MessageEvent)args[3]!;
            return result;
        }
        public bool HasMismatchedAuthorization(EventCallback callback, string app, string bot) =>
            (bool)Invoke(_guard, "HasMismatchedAuthorization", callback, app, bot)!;
    }

    /// <summary>Supplies local Socket Mode Hello frames without claiming real Slack integration.</summary>
    private sealed class SocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _connections = [];
        private readonly Task _accept;
        public string Url { get; }
        public SocketServer()
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Url = $"ws://127.0.0.1:{number}/";
            _listener.Prefixes.Add($"http://127.0.0.1:{number}/");
            _listener.Start();
            _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    _connections.Add(ServeAsync(context));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private async Task ServeAsync(HttpListenerContext context)
        {
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"connection_info\":{\"app_id\":\"A_APP\"}}"),
                    WebSocketMessageType.Text, true, _stop.Token);
                var buffer = new byte[1024];
                while (!_stop.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", _stop.Token);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (WebSocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accept;
            await Task.WhenAll(_connections);
            _listener.Close();
            _stop.Dispose();
        }
    }
}
