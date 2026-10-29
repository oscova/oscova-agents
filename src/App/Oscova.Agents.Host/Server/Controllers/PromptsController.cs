using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.ApiContracts.Prompts;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Secrets;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Utils;
using YamlDotNet.Core;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/prompts")]
[IgnoreAntiforgeryToken]
public sealed class PromptsController(
    UserServiceManager userServiceManager,
    IUserDirectoryService userDirectoryService,
    IContentProtector protector,
    ILogger<PromptsController> logger) : UserScopeControllerBase(userServiceManager)
{
    [HttpGet]
    public async Task<ActionResult<PromptCollection>> Get(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var defaults = userScope.CrewService.InstructionDefaults;

        return Ok(new PromptCollection
        {
            BrowserUse = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.BrowserUsePrompt, defaults.GetValueOrDefault(PromptFileNames.BrowserUsePrompt)),
            Compaction = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.CompactionPrompt, defaults.GetValueOrDefault(PromptFileNames.CompactionPrompt)),
            Memory = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.MemorySystemPrompt, defaults.GetValueOrDefault(PromptFileNames.MemorySystemPrompt)),
            Environment = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.EnvironmentPrompt, defaults.GetValueOrDefault(PromptFileNames.EnvironmentPrompt)),
            Library = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.LibraryPrompt, defaults.GetValueOrDefault(PromptFileNames.LibraryPrompt)),
            ToolSearch = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.ToolSearchPrompt, defaults.GetValueOrDefault(PromptFileNames.ToolSearchPrompt)),
            TodoProvider = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.TodoProviderPrompt, defaults.GetValueOrDefault(PromptFileNames.TodoProviderPrompt)),
            Subagents = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.SubagentsPrompt, defaults.GetValueOrDefault(PromptFileNames.SubagentsPrompt)),
            Skills = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.SkillsPrompt, defaults.GetValueOrDefault(PromptFileNames.SkillsPrompt)),
            AvatarMarkup = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.AvatarMarkupPrompt, defaults.GetValueOrDefault(PromptFileNames.AvatarMarkupPrompt)),
            ChatContinuation = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.ChatContinuationPrompt, defaults.GetValueOrDefault(PromptFileNames.ChatContinuationPrompt)),
            GoalJudge = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.GoalJudgePrompt, defaults.GetValueOrDefault(PromptFileNames.GoalJudgePrompt)),
            GoalContinuation = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.GoalContinuationPrompt, defaults.GetValueOrDefault(PromptFileNames.GoalContinuationPrompt)),
            ChatTitle = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.ChatTitlePrompt, defaults.GetValueOrDefault(PromptFileNames.ChatTitlePrompt)),
            ToolFailureRecovery = await ReadPromptDocumentAsync(userScope.UserId, PromptFileNames.ToolFailureRecoveryPrompt, defaults.GetValueOrDefault(PromptFileNames.ToolFailureRecoveryPrompt))
        });
    }

    [HttpPut("{promptKey}")]
    public async Task<IActionResult> Save(string promptKey, [FromBody] SavePromptRequest request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var relativePath = promptKey switch
        {
            "browser-use" => PromptFileNames.BrowserUsePrompt,
            "compaction" => PromptFileNames.CompactionPrompt,
            "memory" => PromptFileNames.MemorySystemPrompt,
            "environment" => PromptFileNames.EnvironmentPrompt,
            "library" => PromptFileNames.LibraryPrompt,
            "tool-search" => PromptFileNames.ToolSearchPrompt,
            "todo-provider" => PromptFileNames.TodoProviderPrompt,
            "subagents" => PromptFileNames.SubagentsPrompt,
            "skills" => PromptFileNames.SkillsPrompt,
            "avatar-markup" => PromptFileNames.AvatarMarkupPrompt,
            "chat-continuation" => PromptFileNames.ChatContinuationPrompt,
            "goal-judge" => PromptFileNames.GoalJudgePrompt,
            "goal-continuation" => PromptFileNames.GoalContinuationPrompt,
            "chat-title" => PromptFileNames.ChatTitlePrompt,
            "tool-failure-recovery" => PromptFileNames.ToolFailureRecoveryPrompt,
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return NotFound();
        }

        try
        {
            var document = PromptInstructionRecord.Format(request.Mode, request.Content);
            if (System.Text.Encoding.UTF8.GetByteCount(document) > UserFileLimits.MaxEditSizeBytes)
            {
                throw new InvalidOperationException($"Content is too large to save (max {ByteSize.Format(UserFileLimits.MaxEditSizeBytes)}).");
            }

            var ciphertext = protector.Protect(userScope.UserId, EncryptedFileContent.ProtectionPurpose, document);
            await userDirectoryService.WriteFileTextAsync(userScope.UserId, relativePath, ciphertext);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        userScope.AppService.InvalidateAllCachedAgents();
        return NoContent();
    }

    /// <summary>Reads the saved instruction while retaining the same default for draft restoration.</summary>
    private async Task<PromptDocument> ReadPromptDocumentAsync(string userId, string relativePath, string? defaultValue)
    {
        var result = new PromptDocument
        {
            RelativePath = relativePath,
            FileName = Path.GetFileNameWithoutExtension(relativePath),
            Mode = InstructionSelection.Default,
            DefaultContent = defaultValue
        };
        if (!userDirectoryService.FileExists(userId, relativePath)) { return result; }

        // Read/decrypt failures remain real request failures, only rejected metadata is item-local.
        var document = await userDirectoryService.ReadFileTextForRuntimeAsync(userId, relativePath);
        try
        {
            var record = PromptInstructionRecord.Parse(document);
            result.Mode = record.Mode;
            result.Content = record.Content;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or YamlException)
        {
            result.Mode = null;
            result.HasMetadataError = true;
            // Parser messages can quote persisted content, retain the origin without logging that content.
            logger.LogWarning("Instruction metadata rejected for {UserId} at {RelativePath}. {ExceptionType}: {ExceptionStackTrace}",
                userId, relativePath, ex.GetType().Name, ex.StackTrace);
        }

        return result;
    }
}
