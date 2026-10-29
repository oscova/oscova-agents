using System.Net;
using System.Text;
using Oscova.Agents.Plugins.Tests.TestSupport;
using System.Reflection;
using NuGet.Packaging;
using Oscova.Agents.Channels;
using Oscova.Agents.Plugins.Telegram;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.Telegram;

/// <summary>Checks Telegram declarations, protocol translation, and package boundaries.</summary>
public sealed class TelegramChannelTests
{
    [Fact]
    public async Task Reply_TransportFailureDoesNotExposeBotToken()
    {
        const string token = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";
        var received = new TaskCompletionSource<ChannelMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = 0;
        using var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var method = request.RequestUri!.Segments[^1];
            if (method == "sendMessage")
            {
                throw new HttpRequestException($"Request failed: {request.RequestUri}");
            }
            if (method == "getUpdates" && Interlocked.Increment(ref updates) > 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            var json = method == "getMe"
                ? """{"ok":true,"result":{"id":123456789,"is_bot":true,"first_name":"Fixture","username":"fixture_bot"}}"""
                : """{"ok":true,"result":[{"update_id":1,"message":{"message_id":1,"date":1,"chat":{"id":42,"type":"private"},"from":{"id":42,"is_bot":false,"first_name":"Sender"},"text":"hello"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        await using var connection = new TelegramChannelConnection(
            PluginTestContext.Create(new MissingCredentials(token), Path.GetTempPath(), Path.GetTempPath()),
            PluginTestContext.Channel((message, _) => { received.TrySetResult(message); return Task.CompletedTask; },
                transport: new HttpClient(handler)), new TelegramSettings { AuthorizedUserId = "42" });
        await connection.StartAsync(CancellationToken.None);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var error = await Record.ExceptionAsync(() => message.ReplyAsync("reply", CancellationToken.None));

        Assert.NotNull(error);
        Assert.DoesNotContain(token, error.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task SuccessfulUpdateAfterPollingError_ReportsConnected()
    {
        var statuses = new List<ChannelConnectionStatus>();
        ChannelMessage? received = null;
        await using var connection = new TelegramChannelConnection(
            PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath()),
            PluginTestContext.Channel((message, _) => { received = message; return Task.CompletedTask; },
                status => { statuses.Add(status); return Task.CompletedTask; }), new TelegramSettings { AuthorizedUserId = "42" });
        typeof(TelegramChannelConnection).GetField("_botUsername", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connection, "sample_bot");
        var error = typeof(TelegramChannelConnection).GetMethod("OnPollingErrorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)error.Invoke(connection, [new HttpRequestException("temporary failure"), CancellationToken.None])!;
        Assert.Equal(ChannelConnectionState.Error, Assert.Single(statuses).State);

        var update = new Update { Message = new Message
        {
            Id = 1, Chat = new Chat { Id = 7, Type = ChatType.Private },
            From = new User { Id = 42, FirstName = "Sender" }, Text = "recovered"
        } };
        var receive = typeof(TelegramChannelConnection).GetMethod("OnUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)receive.Invoke(connection, [update, CancellationToken.None])!;

        Assert.Equal("recovered", received?.Text);
        Assert.Equal(ChannelConnectionState.Connected, statuses[^1].State);
        Assert.Equal("sample_bot", statuses[^1].BotName);
        Assert.Null(statuses[^1].Error);
        await (Task)receive.Invoke(connection, [update, CancellationToken.None])!;
        Assert.Equal(2, statuses.Count);
    }

    /// <summary>Preserves exact signed sender IDs and incomplete disabled configuration.</summary>
    [Theory]
    [InlineData("9223372036854775807", true, true)]
    [InlineData("9223372036854775808", true, false)]
    [InlineData("0", false, false)]
    [InlineData("@username", false, false)]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    public void SettingsValidation_ChecksPositiveSignedIds(string value, bool complete, bool valid)
    {
        Assert.Equal(valid, new TelegramSettings { AuthorizedUserId = value }.Validate(complete).Count == 0);
    }

    /// <summary>Filters vendor messages and carries attachment metadata without downloading.</summary>
    [Theory]
    [InlineData(42L, ChatType.Private, false, true)]
    [InlineData(42L, ChatType.Private, true, true)]
    [InlineData(43L, ChatType.Private, false, false)]
    [InlineData(42L, ChatType.Group, false, false)]
    public async Task InboundEvent_PreservesPrivateSenderAndAttachmentMetadata(long sender, ChatType chatType, bool bot, bool accepted)
    {
        ChannelMessage? received = null;
        await using var connection = CreateConnection(message => received = message);
        var update = new Update
        {
            Message = new Message
            {
                Id = 12, Chat = new Chat { Id = 7, Type = chatType },
                From = new User { Id = sender, IsBot = bot, FirstName = "Sender" }, Caption = "caption",
                Document = new Document { FileId = "document", FileUniqueId = "unique", FileName = "notes.txt" },
                Photo =
                [
                    new() { FileId = "small", FileUniqueId = "small", Width = 1, Height = 1, FileSize = 10 },
                    new() { FileId = "large", FileUniqueId = "large", Width = 20, Height = 20, FileSize = 30 }
                ]
            }
        };
        await (Task)typeof(TelegramChannelConnection).GetMethod("OnUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [update, CancellationToken.None])!;
        Assert.Equal(accepted, received is not null);
        if (received is not null)
        {
            Assert.Equal("caption", received.Text);
            Assert.Equal("7", received.ConversationId);
            Assert.Equal(bot, received.IsBot);
            Assert.Equal(2, received.Attachments.Count);
            Assert.Equal(0, received.Attachments[0].Size);
            Assert.Equal("application/octet-stream", received.Attachments[0].ContentType);
            Assert.Equal("telegram-photo-12.jpg", received.Attachments[1].FileName);
            Assert.Equal(30, received.Attachments[1].Size);
        }
        await connection.StopAsync(CancellationToken.None);
        await connection.DisposeAsync();
    }

    /// <summary>Sends task notifications to the authorized user's private chat.</summary>
    [Fact]
    public async Task Notification_SendsToAuthorizedPrivateChat()
    {
        await using var connection = CreateConnection(_ => { });
        var client = new RecordingClient();
        typeof(TelegramChannelConnection).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, client);
        await connection.SendNotificationAsync("Task ready", CancellationToken.None);
        Assert.Equal(["Task ready"], client.Chunks);
        Assert.Equal([42L], client.ChatIds);
    }

    /// <summary>Exercises the reply callback and preserves all Unicode text within Telegram's limit.</summary>
    [Fact]
    public async Task Reply_SendsCompleteUnicodeChunks()
    {
        ChannelMessage? received = null;
        await using var connection = CreateConnection(message => received = message);
        var client = new RecordingClient();
        typeof(TelegramChannelConnection).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, client);
        var update = new Update { Message = new Message
        {
            Id = 1, Chat = new Chat { Id = 7, Type = ChatType.Private },
            From = new User { Id = 42, FirstName = "Sender" }, Text = "hello"
        } };
        await (Task)typeof(TelegramChannelConnection).GetMethod("OnUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [update, CancellationToken.None])!;
        var text = new string('x', 4095) + "\U0001F600" + new string('y', 1000);
        await received!.ReplyAsync(text, CancellationToken.None);
        Assert.Equal(text, string.Concat(client.Chunks));
        Assert.All(client.Chunks, chunk =>
        {
            Assert.InRange(chunk.Length, 1, 4096);
            Assert.False(char.IsHighSurrogate(chunk[^1]));
            Assert.False(char.IsLowSurrogate(chunk[0]));
        });
    }

    /// <summary>Loads the real plugin without connecting and respects disabled-plugin absence.</summary>
    [Fact]
    public async Task LoadedPlugin_DeclaresNotifyingChannelAndRequiresTokenAtStart()
    {
        using var root = TestPluginRoot.Create();
        root.AddPluginFromProjectOutput(Path.Combine("src", "Plugins", "Oscova.Agents.Plugins.Telegram"), "oscova.telegram");
        Assert.Empty(AgentPluginLoader.LoadDirectory(root.RootPath, new HashSet<string>()).Plugins);
        var catalog = AgentPluginLoader.LoadDirectory(root.RootPath);
        Assert.Empty(catalog.Diagnostics);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("telegram-bot", Assert.Single(plugin.DeclaredCredentialKeys));
        var contribution = Assert.Single(plugin.Channels);
        Assert.True(contribution.Definition.SupportsNotifications);
        Assert.Single(plugin.SettingsPanels);
        var services = PluginTestContext.Create(new MissingCredentials(), plugin.PluginDirectory, Path.GetTempPath(),
            settings: PluginTestContext.Settings(new TelegramSettings { AuthorizedUserId = "42" }));
        var snapshot = await contribution.Channel.ReadConfigurationAsync(services, true);
        await using var connection = snapshot.CreateConnection(PluginTestContext.Channel());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.StartAsync(CancellationToken.None));
        Assert.Contains("in Credentials", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Checks the package includes its manifest and vendor dependency without private assemblies.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Package_DeclaresTelegramDependencyAndExcludesPrivateAssemblies()
    {
        var directory = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? throw new InvalidOperationException("Set OSCOVA_NUGET_PACKAGE_DIRECTORY to the reviewed local feed.");
        using var package = new PackageArchiveReader(Path.Combine(directory, $"Oscova.Agents.Plugins.Telegram.{PackageVersions.Get("Oscova.Agents.Plugins.Telegram")}.nupkg"));
        var files = package.GetFiles().ToArray();
        Assert.Contains("oscova-plugin.json", files);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.Telegram.dll", files);
        Assert.DoesNotContain(files, file => file.Contains("Oscova.Agents.Core", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Infrastructure", StringComparison.Ordinal) || file.Contains("Oscova.Agents.Host", StringComparison.Ordinal));
        var dependencies = package.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages).ToArray();
        Assert.Equal(new[] { "Telegram.Bot", "Oscova.Agents" }.Order(StringComparer.Ordinal),
            dependencies.Select(dependency => dependency.Id).Order(StringComparer.Ordinal));
        var dependency = Assert.Single(dependencies, dependency => dependency.Id == "Telegram.Bot");
        Assert.Equal("Telegram.Bot", dependency.Id);
        Assert.Equal(PackageVersions.GetReferenced(typeof(TelegramBotClient)), dependency.VersionRange.MinVersion!.ToNormalizedString());
    }

    private static TelegramChannelConnection CreateConnection(Action<ChannelMessage> receive) => new(
        PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath()),
        PluginTestContext.Channel((message, _) => { receive(message); return Task.CompletedTask; }, _ => Task.CompletedTask), new TelegramSettings { AuthorizedUserId = "42" });

    private sealed class MissingCredentials(string? token = null) : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult(token);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }

    private sealed class RecordingClient() : TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")
    {
        public List<string> Chunks { get; } = [];
        public List<long?> ChatIds { get; } = [];
        public override Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var send = Assert.IsType<SendMessageRequest>(request);
            Chunks.Add(send.Text);
            ChatIds.Add(send.ChatId.Identifier);
            return Task.FromResult((TResponse)(object)new Message { Id = Chunks.Count, Chat = new Chat { Id = 7, Type = ChatType.Private } });
        }
    }
}
