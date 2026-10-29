using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Host.Services.Api;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json;

namespace Oscova.Agents.App.Cli;

public static class CliPromptResponder
{
    private const int MaxArgumentLength = 240;

    public static async Task RespondToElicitationAsync(ChatApiClient chatApi, ChatElicitationRequestedEvent payload, bool noInteractive, CancellationToken cancellationToken)
    {
        var request = payload.Message.Elicitation;
        if (request is null)
        {
            return;
        }

        if (noInteractive)
        {
            await SubmitCancellationAsync(chatApi, request, cancellationToken).ConfigureAwait(false);
            return;
        }

        RenderCard("Agent needs input", new Text(request.Message, CliTheme.Value), CliTheme.Accent);

        switch (request.SchemaType)
        {
            case ElicitationSchemaType.Boolean:
                var booleanField = request.BooleanFields.FirstOrDefault();
                var approved = AnsiConsole.Confirm(BuildPrompt(request.Message, booleanField?.Label));
                await chatApi.SubmitBooleanElicitationAsync(request.RequestId, approved, isCancel: false, cancellationToken).ConfigureAwait(false);
                break;
            case ElicitationSchemaType.Number:
                var numberValues = request.NumberFields.ToDictionary(
                    field => field.Name,
                    field => new NumberElicitationValue
                    {
                        // Serialize each numeric type before the conditional can widen integers to double.
                        Value = field.IsInteger
                            ? JsonSerializer.SerializeToElement(AnsiConsole.Prompt(new TextPrompt<long>(BuildPrompt(request.Message, field.Label)).DefaultValue(Convert.ToInt64(field.Default ?? 0))))
                            : JsonSerializer.SerializeToElement(AnsiConsole.Prompt(new TextPrompt<double>(BuildPrompt(request.Message, field.Label)).DefaultValue(field.Default ?? 0))),
                        IsInteger = field.IsInteger
                    },
                    StringComparer.OrdinalIgnoreCase);
                await chatApi.SubmitNumberElicitationAsync(request.RequestId, numberValues, isCancel: false, cancellationToken).ConfigureAwait(false);
                break;
            case ElicitationSchemaType.Enum:
                var enumValues = request.EnumFields.ToDictionary(
                    field => field.Name,
                    field => AnsiConsole.Prompt(new SelectionPrompt<string>().Title(BuildPrompt(request.Message, field.Label)).AddChoices(field.Options.Select(option => option.Value))),
                    StringComparer.OrdinalIgnoreCase);
                await chatApi.SubmitEnumElicitationAsync(request.RequestId, enumValues, isCancel: false, cancellationToken).ConfigureAwait(false);
                break;
            default:
                var stringValues = request.StringFields.ToDictionary(
                    field => field.Name,
                    field => AnsiConsole.Prompt(new TextPrompt<string>(BuildPrompt(request.Message, field.Label)).AllowEmpty()),
                    StringComparer.OrdinalIgnoreCase);
                await chatApi.SubmitStringElicitationAsync(request.RequestId, stringValues, isCancel: false, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Submits a tool approval decision and returns the value sent to the host.
    /// </summary>
    public static async Task<bool> RespondToToolApprovalAsync(ChatApiClient chatApi, ChatToolApprovalRequestedEvent payload, bool noInteractive, CancellationToken cancellationToken)
    {
        var request = payload.Message.ToolApproval;
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return false;
        }

        if (noInteractive)
        {
            await chatApi.SubmitToolApprovalAsync(request.RequestId, ToolApprovalDecision.Deny, cancellationToken).ConfigureAwait(false);
            return false;
        }

        RenderToolApprovalCard(request);

        // A selection list needs ANSI; plain terminals keep the yes/no confirmation.
        var approved = AnsiConsole.Profile.Capabilities.Ansi
            ? string.Equals(
                AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Run this tool?").AddChoices("Deny", "Allow")),
                "Allow",
                StringComparison.Ordinal)
            : AnsiConsole.Confirm("Run this tool?", defaultValue: false);

        await chatApi.SubmitToolApprovalAsync(request.RequestId, approved ? ToolApprovalDecision.AllowOnce : ToolApprovalDecision.Deny, cancellationToken).ConfigureAwait(false);
        return approved;
    }

    /// <summary>
    /// Shows what the agent wants to run, and with which arguments, before the approval choice.
    /// </summary>
    private static void RenderToolApprovalCard(ToolApprovalRequest request)
    {
        var body = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        body.AddRow(new Text("tool", CliTheme.Label), new Text(request.ToolName, CliTheme.Value));
        bool shellReview = request.Review?.Kind is "shell" or "skill-script";
        if (shellReview)
        {
            body.AddRow(new Text("review", CliTheme.Label), new Text(request.Message, CliTheme.Value));
        }

        foreach (var (name, value) in ArgumentRows(request.Arguments, truncate: !shellReview))
        {
            body.AddRow(new Text(name, CliTheme.Label), new Text(value, CliTheme.Value));
        }

        RenderCard("Tool approval", body, CliTheme.Warning);
    }

    private static IEnumerable<(string Name, string Value)> ArgumentRows(JsonElement? arguments, bool truncate)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            var raw = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
            yield return (property.Name, truncate && raw.Length > MaxArgumentLength ? raw[..MaxArgumentLength] + "..." : raw);
        }
    }

    private static void RenderCard(string header, IRenderable body, Color accent)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(CliRender.Card(header, body, accent));
    }

    private static async Task SubmitCancellationAsync(ChatApiClient chatApi, ElicitationRequest request, CancellationToken cancellationToken)
    {
        switch (request.SchemaType)
        {
            case ElicitationSchemaType.Boolean:
                await chatApi.SubmitBooleanElicitationAsync(request.RequestId, null, isCancel: true, cancellationToken).ConfigureAwait(false);
                break;
            case ElicitationSchemaType.Number:
                await chatApi.SubmitNumberElicitationAsync(request.RequestId, new Dictionary<string, NumberElicitationValue>(), isCancel: true, cancellationToken).ConfigureAwait(false);
                break;
            case ElicitationSchemaType.Enum:
                await chatApi.SubmitEnumElicitationAsync(request.RequestId, new Dictionary<string, string>(), isCancel: true, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await chatApi.SubmitStringElicitationAsync(request.RequestId, new Dictionary<string, string>(), isCancel: true, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static string BuildPrompt(string message, string? label) =>
        string.IsNullOrWhiteSpace(label) ? message : $"{message} ({label})";
}
