namespace Oscova.Agents.Plugins.Tests.TestSupport;

/// <summary>Supplies immediate responses or cancellable asynchronous HTTP boundaries to plugin tests.</summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request))) { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => responder(request, cancellationToken);
}
