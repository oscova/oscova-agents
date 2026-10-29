using Microsoft.Agents.AI;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Definitions;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Memory;
using Oscova.Agents.Secrets;
using System.Text;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Loads an agent's encrypted private self-improvement notes into its instructions.</summary>
public sealed class PrivateNotesContextProvider(
    string userId,
    IUserDirectoryService userDirectoryService,
    IContentProtector contentProtector,
    AgentDefinition agentDefinition,
    IUserFileBackupService fileBackups) : AIContextProvider
{
    /// <summary>Guides ordinary chats and reflection through the same private lesson workflow.</summary>
    public const string Instructions = """
        - Use notes find/read to apply relevant behavioral lessons.
        - Save evidenced lessons with notes save. New concepts require frontmatter.type.
        - Read matching lessons before updating. Skip unchanged duplicates.
        """;
    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (agentDefinition.IsShared)
        {
            return new AIContext();
        }

        string? content;
        try
        {
            content = await AgentNotesFile
                .ReadAsync(userId, agentDefinition.Id, userDirectoryService, contentProtector, fileBackups, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return new AIContext();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new AIContext { Instructions = Instructions };
        }

        var builder = new StringBuilder();
        builder.AppendLine(Instructions).AppendLine("<private_notes>")
            .AppendLine(KnowledgeContextProjection.KeepHead(content))
            .Append("</private_notes>");

        return new AIContext
        {
            Instructions = builder.ToString()
        };
    }
}
