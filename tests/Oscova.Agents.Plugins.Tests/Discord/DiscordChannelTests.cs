using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Discord;
using Discord.WebSocket;
using Oscova.Agents.Channels;
using Oscova.Agents.Tests.TestSupport.TestPlugin;
using NuGet.Packaging;
using Oscova.Agents.Plugins.Discord;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.Discord;

public sealed class DiscordChannelTests
{
    [Theory]
    [InlineData("123", true, true)]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    [InlineData("0", false, false)]
    [InlineData("18446744073709551616", true, false)]
    public void SettingsValidation_PreservesUnsignedSenderIds(string value, bool complete, bool valid)
    {
        var errors = new DiscordSettings { AuthorizedUserId = value }.Validate(complete);
        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData(123UL, false, false, true)]
    [InlineData(456UL, false, false, false)]
    [InlineData(123UL, true, false, true)]
    [InlineData(123UL, true, true, false)]
    public async Task InboundEvent_MatchesConfiguredSenderAndSuppressesSelf(
        ulong senderId, bool isBot, bool isSelf, bool accepted)
    {
        ChannelMessage? received = null;
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath());
        await using var connection = new DiscordChannelConnection(context, PluginTestContext.Channel((message, _) => { received = message; return Task.CompletedTask; }, _ => Task.CompletedTask), new DiscordSettings { AuthorizedUserId = "123" });
        var client = (DiscordSocketClient)typeof(DiscordChannelConnection)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        if (isSelf)
        {
            var self = (SocketSelfUser)RuntimeHelpers.GetUninitializedObject(typeof(SocketSelfUser));
            SetField(typeof(SocketEntity<ulong>), self, "<Id>k__BackingField", senderId);
            typeof(BaseSocketClient).GetProperty(nameof(BaseSocketClient.CurrentUser), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.SetValue(client, self);
        }
        // SDK event models have internal constructors. Populate only the protocol data consumed by the adapter.
        var author = (SocketUnknownUser)RuntimeHelpers.GetUninitializedObject(typeof(SocketUnknownUser));
        SetField(typeof(SocketEntity<ulong>), author, "<Id>k__BackingField", senderId);
        SetField(typeof(SocketUnknownUser), author, "<Username>k__BackingField", "sender");
        SetField(typeof(SocketUnknownUser), author, "<IsBot>k__BackingField", isBot);
        var channel = (SocketDMChannel)RuntimeHelpers.GetUninitializedObject(typeof(SocketDMChannel));
        SetField(typeof(SocketEntity<ulong>), channel, "<Id>k__BackingField", 789UL);
        var message = (SocketUserMessage)RuntimeHelpers.GetUninitializedObject(typeof(SocketUserMessage));
        SetField(typeof(SocketMessage), message, "<Author>k__BackingField", author);
        SetField(typeof(SocketMessage), message, "<Channel>k__BackingField", channel);
        SetField(typeof(SocketMessage), message, "<Content>k__BackingField", "hello");
        SetField(typeof(SocketUserMessage), message, "_attachments", ImmutableArray<Attachment>.Empty);
        var callback = typeof(DiscordChannelConnection).GetMethod("OnMessageReceivedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)callback.Invoke(connection, [message])!;
        Assert.Equal(accepted, received is not null);
        if (accepted)
        {
            Assert.Equal(isBot, received!.IsBot);
            Assert.Equal("789", received.ConversationId);
        }
    }

    [Fact]
    public async Task GatewayResume_RestoresConnectedStatus()
    {
        var statuses = new List<ChannelConnectionStatus>();
        var context = PluginTestContext.Create(new MissingCredentials(), Path.GetTempPath(), Path.GetTempPath());
        await using var connection = new DiscordChannelConnection(context, PluginTestContext.Channel(status: status =>
        {
            statuses.Add(status);
            return Task.CompletedTask;
        }), new DiscordSettings { AuthorizedUserId = "123" });
        var client = (DiscordSocketClient)typeof(DiscordChannelConnection)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;

        await RaiseClientEventAsync<Func<Exception, Task>>(client, "_disconnectedEvent", handler => handler(new TimeoutException()));
        Assert.Equal(ChannelConnectionState.Error, statuses[^1].State);
        // A gateway RESUMED dispatch raises only Connected, never Ready.
        await RaiseClientEventAsync<Func<Task>>(client, "_connectedEvent", handler => handler());

        Assert.Equal(ChannelConnectionState.Connected, statuses[^1].State);
    }

    [Fact]
    public async Task LoadedPlugin_DeclaresChannelAndCredentials_AndFailsWithoutToken()
    {
        using var root = TestPluginRoot.Create();
        root.AddPluginFromProjectOutput(Path.Combine("src", "Oscova.Agents.Plugins.Discord"), "oscova.discord");
        Assert.Empty(AgentPluginLoader.LoadDirectory(root.RootPath, new HashSet<string>()).Plugins);
        var catalog = AgentPluginLoader.LoadDirectory(root.RootPath);
        Assert.Empty(catalog.Diagnostics);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("discord-bot", Assert.Single(plugin.DeclaredCredentialKeys));
        Assert.Single(plugin.Credentials);
        var contribution = Assert.Single(plugin.Channels);
        Assert.Single(plugin.SettingsPanels);
        var services = PluginTestContext.Create(new MissingCredentials(), plugin.PluginDirectory, Path.GetTempPath(),
            settings: PluginTestContext.Settings(new DiscordSettings { AuthorizedUserId = "123" }));
        var snapshot = await contribution.Channel.ReadConfigurationAsync(services, true);
        await using var connection = snapshot.CreateConnection(PluginTestContext.Channel());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.StartAsync(CancellationToken.None));
        Assert.Contains("in Credentials", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Package_ContainsOnlyPluginAssemblyAndDeclaresDiscordDependency()
    {
        var directory = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? throw new InvalidOperationException("Set OSCOVA_NUGET_PACKAGE_DIRECTORY to the reviewed local feed.");
        using var package = new PackageArchiveReader(Path.Combine(directory, $"Oscova.Agents.Plugins.Discord.{PackageVersions.Get("Oscova.Agents.Plugins.Discord")}.nupkg"));
        var files = package.GetFiles().ToArray();
        Assert.Contains("oscova-plugin.json", files);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.Discord.dll", files);
        Assert.DoesNotContain(files, file => file.Contains("Oscova.Agents.Core", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Infrastructure", StringComparison.Ordinal) ||
            file.Contains("Oscova.Agents.Host", StringComparison.Ordinal));
        var dependencies = package.NuspecReader.GetDependencyGroups().SelectMany(group => group.Packages).ToArray();
        Assert.Equal(new[] { "Discord.Net.WebSocket", "Oscova.Agents" }.Order(StringComparer.Ordinal),
            dependencies.Select(dependency => dependency.Id).Order(StringComparer.Ordinal));
        var dependency = Assert.Single(dependencies, dependency => dependency.Id == "Discord.Net.WebSocket");
        Assert.Equal("Discord.Net.WebSocket", dependency.Id);
        Assert.Equal(PackageVersions.GetReferenced(typeof(DiscordSocketClient)), dependency.VersionRange.MinVersion!.ToNormalizedString());
    }

    private static void SetField(Type type, object instance, string name, object value) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    // The SDK raises gateway events internally. Invoke the adapter's real subscriptions on the client.
    private static async Task RaiseClientEventAsync<THandler>(DiscordSocketClient client, string field, Func<THandler, Task> raise)
    {
        var asyncEvent = typeof(DiscordSocketClient).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var handlers = (IReadOnlyList<THandler>)asyncEvent.GetType().GetProperty("Subscriptions")!.GetValue(asyncEvent)!;
        foreach (var handler in handlers) { await raise(handler); }
    }

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }
}
