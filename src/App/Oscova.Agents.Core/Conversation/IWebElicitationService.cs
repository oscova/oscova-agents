using System.Text.Json;
using Oscova.Agents.Conversations;
using ModelContextProtocol.Protocol;

namespace Oscova.Agents.Core.Conversation;

public interface IWebElicitationService
{
    Task SubmitBooleanElicitationAsync(Guid requestId, bool? value, bool isCancel);

    Task SubmitStringElicitationAsync(Guid requestId, IReadOnlyDictionary<string, string> values, bool isCancel);

    Task SubmitNumberElicitationAsync(Guid requestId, IReadOnlyDictionary<string, (JsonElement Value, bool IsInteger)> values, bool isCancel);

    Task SubmitEnumElicitationAsync(Guid requestId, IReadOnlyDictionary<string, string> values, bool isCancel);

    Task<ElicitResult> HandleMcpElicitationAsync(
        ElicitRequestParams requestParams,
        ChatThread chatThread,
        Action onStateChanged,
        CancellationToken token);

    void CancelPendingElicitation(Guid requestId);

    void CancelAllPendingElicitations();
}
