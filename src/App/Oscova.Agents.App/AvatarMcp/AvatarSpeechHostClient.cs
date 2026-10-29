using Microsoft.Extensions.Logging;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Avatar;
using Oscova.Agents.Core.ApiContracts.AvatarBridge;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Infrastructure.Utils;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Oscova.Agents.Core.ApiContracts.Avatar.AvatarBridgeEndpointResult;

namespace Oscova.Agents.App.AvatarMcp;

/// <summary>Calls the matching Assistant host to synthesize and queue Avatar speech.</summary>
public sealed class AvatarSpeechHostClient(
    StartCollisionChecker hosts,
    CliAuthFileStore auth,
    HostApiClient clients,
    ILogger<AvatarSpeechHostClient> logger)
{
    /// <summary>Uses the saved operator login to request text speech from the local host.</summary>
    public async Task<AvatarBridgeEndpointResult> SpeakAsync(string text, CancellationToken ct)
    {
        try
        {
            var runtime = await hosts.FindRunningHostAsync(ct).ConfigureAwait(false);
            if (runtime is null)
            {
                return Failure("host_stopped", "Start Oscova Assistant, then retry this tool.");
            }

            // Resolve authentication for the running local host, independently of the CLI's current host.
            var host = CliHostResolver.NormalizeHostUrl(CliHostUrlSelector.SelectLocalHost(runtime.Urls));
            var entry = await auth.FindAsync(host, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(entry?.Token))
            {
                return Failure("operator_login_required", $"Run 'oscova login --host {host}' and sign in as the operator.");
            }

            using var client = clients.Create(new ResolvedHost(host, entry.Token, entry));
            using var response = await client.PostAsJsonAsync(ApiRoutes.AvatarBridge.Speech.Speak,
                new AvatarSpeakRequest { Text = text, ExpectedAppDataPath = PathUtil.AppDataPath }, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failure("operator_login_required", $"Run 'oscova login --host {host}' and sign in as the operator.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Failure("invalid_host_response", "The Assistant returned an unexpected response. Check its log before retrying because speech may already have been queued.");
            }

            // Avatar endpoints can report an operation failure with HTTP 200.
            return new(!response.IsSuccessStatusCode || !success.GetBoolean(), payload.Clone());
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Avatar speech host request could not complete.");
            return Failure("host_unreachable", "The Assistant request did not complete. Check that the host is running and inspect the Engine before retrying. No automatic retry was performed.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Avatar speech host returned invalid JSON.");
            return Failure("invalid_host_response", "The Assistant returned an unexpected response. Check its log before retrying because speech may already have been queued.");
        }
    }
}
