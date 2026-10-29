using System.Text.Json;
using System.Text.Json.Serialization;
using Oscova.Agents.Hooks;

namespace Oscova.Agents.Infrastructure.Hooks;

internal static class HookJsonSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    static HookJsonSerializer()
    {
        Options.Converters.Add(new JsonStringEnumConverter<HookEventKind>());
        Options.Converters.Add(new JsonStringEnumConverter<HookDecision>());
        Options.Converters.Add(new JsonStringEnumConverter<HookFailureMode>());
        Options.Converters.Add(new JsonStringEnumConverter<HookHandlerType>());
    }
}