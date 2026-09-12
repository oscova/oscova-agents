using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Skills;

/// <summary>
/// Imports one packaged skill into a user's skills directory after validating its layout.
/// </summary>
public interface ISkillImportService
{
    /// <summary>
    /// Imports a single markdown or zip skill package for the specified user.
    /// </summary>
    Task<SkillImportResult> ImportAsync(string userId, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a complete agent-authored SKILL.md document after stamping trusted provenance.
    /// </summary>
    Task<SkillImportResult> ImportAgentAuthoredMarkdownAsync(
        string userId,
        string content,
        AgentAuthoredSkillContext authoring,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing agent-authored SKILL.md document after validating ownership provenance.
    /// </summary>
    Task<SkillImportResult> ReplaceAgentAuthoredMarkdownAsync(
        string userId,
        string skillName,
        string content,
        AgentAuthoredSkillContext authoring,
        CancellationToken cancellationToken = default);
}
