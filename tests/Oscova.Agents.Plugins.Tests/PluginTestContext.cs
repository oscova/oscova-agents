using Oscova.Agents.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins.Tests;

/// <summary>Supplies guarded-contract test services to plugin contract tests.</summary>
internal static class PluginTestContext
{
    public static AgentPluginContext Create(IToolCredentialResolver credentials, string pluginDirectory,
        string modelDirectory, Func<string, CancellationToken, Task<string?>>? token = null,
        Func<HttpMessageHandler>? handler = null, IPluginSettings? settings = null, TimeProvider? clock = null)
        => new(credentials, new Services(token, handler), NullLoggerFactory.Instance, clock ?? TimeProvider.System,
            pluginDirectory, modelDirectory) { UserSettings = settings };

    public static ChannelConnectionContext Channel(Func<ChannelMessage, CancellationToken, Task>? receive = null,
        Func<ChannelConnectionStatus, Task>? status = null,
        HttpClient? transport = null)
        => new(new Sink(receive, status), transport ?? new HttpClient());

    public static IPluginSettings Settings<T>(T model) => new ModelSettings(new YamlConverter().Serialize(model));

    private sealed class ModelSettings(string yaml) : IPluginSettings
    {
        public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(yaml);
        public Task WriteAsync(string key, string content, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Services(Func<string, CancellationToken, Task<string?>>? token, Func<HttpMessageHandler>? handler) : IAgentPluginServices
    {
        public Oscova.Agents.Toolsets.IToolOutputStore? ToolOutputStore => null;
        public IPluginEncryptionService Encryption
            => throw new NotSupportedException("Plugin encryption is not configured in this fixture.");
        public HttpClient CreateHttpClient() => new(CreateHttpMessageHandler());
        public HttpMessageHandler CreateHttpMessageHandler() => handler?.Invoke() ?? new HttpClientHandler();
        public Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken ct = default)
            => token?.Invoke(providerId, ct) ?? Task.FromResult<string?>(null);
    }

    private sealed class Sink(Func<ChannelMessage, CancellationToken, Task>? receive,
        Func<ChannelConnectionStatus, Task>? status) : IChannelEventSink
    {
        public Task ReceiveAsync(ChannelMessage message, CancellationToken ct)
            => receive?.Invoke(message, ct) ?? Task.CompletedTask;
        public Task StatusChangedAsync(ChannelConnectionStatus state)
            => status?.Invoke(state) ?? Task.CompletedTask;
    }
}
