using Oscova.Agents.Skills;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Applies account policy to existing upload and agent-authoring entry points.</summary>
public sealed class GovernedSkillImporter(SkillImportService inner, SkillGovernanceService governance) : ISkillImportService
{
    public async Task<SkillImportResult> ImportAsync(string userId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await governance.RequireEditableAsync(userId, null, cancellationToken).ConfigureAwait(false);
        return await inner.ImportAsync(userId, fileName, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SkillImportResult> ImportAgentAuthoredMarkdownAsync(string userId, string content,
        AgentAuthoredSkillContext authoring, CancellationToken cancellationToken = default)
    {
        await governance.RequireEditableAsync(userId, null, cancellationToken).ConfigureAwait(false);
        return await inner.ImportAgentAuthoredMarkdownAsync(userId, content, authoring, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SkillImportResult> ReplaceAgentAuthoredMarkdownAsync(string userId, string skillName, string content,
        AgentAuthoredSkillContext authoring, CancellationToken cancellationToken = default)
    {
        await governance.RequireEditableAsync(userId, skillName, cancellationToken).ConfigureAwait(false);
        return await inner.ReplaceAgentAuthoredMarkdownAsync(userId, skillName, content, authoring, cancellationToken).ConfigureAwait(false);
    }
}
