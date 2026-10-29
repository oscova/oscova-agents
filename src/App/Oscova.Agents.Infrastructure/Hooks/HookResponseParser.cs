using Oscova.Agents.Hooks;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Hooks;

internal static class HookResponseParser
{
    public static HookDispatchResult FromCommand(int exitCode, string stdout, string stderr)
    {
        var explicitResult = TryParse(stdout);
        return explicitResult ?? exitCode switch
        {
            0 => HookDispatchResult.Allow(),
            2 => HookDispatchResult.Deny(FirstNonEmpty(stderr, stdout, "Hook denied execution."), "command_denied"),
            3 => HookDispatchResult.Deny(FirstNonEmpty(stderr, stdout, "Hook deferral is not supported."), "deferral_not_supported"),
            _ => HookDispatchResult.TransportFailure(FirstNonEmpty(stderr, stdout, "Hook command failed."), "command_failed")
        };
    }

    public static HookDispatchResult FromHttp(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return TryParse(body) ?? HookDispatchResult.Allow();
        }

        return HookDispatchResult.TransportFailure(
            FirstNonEmpty(body, response.ReasonPhrase, "Hook HTTP request failed."),
            $"http_{(int)response.StatusCode}");
    }

    private static HookDispatchResult? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<HookHandlerResponse>(json, HookJsonSerializer.Options);
            if (response is null || string.IsNullOrWhiteSpace(response.Decision))
            {
                return InvalidResponse();
            }

            if (response.Decision.Equals("allow", StringComparison.OrdinalIgnoreCase))
            {
                return HookDispatchResult.Allow();
            }

            if (response.Decision.Equals("deny", StringComparison.OrdinalIgnoreCase))
            {
                return HookDispatchResult.Deny(response.Message ?? "Hook denied execution.", response.Reason);
            }

            if (response.Decision.Equals("defer", StringComparison.OrdinalIgnoreCase))
            {
                return HookDispatchResult.Deny(response.Message ?? "Hook deferral is not supported.", "deferral_not_supported");
            }

            return HookDispatchResult.TransportFailure("Hook returned an unsupported decision.", "unsupported_decision");
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private static HookDispatchResult InvalidResponse() =>
        HookDispatchResult.TransportFailure("Hook returned an invalid response.", "invalid_response");

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private sealed class HookHandlerResponse
    {
        public string? Decision { get; set; }
        public string? Reason { get; set; }
        public string? Message { get; set; }
    }
}
