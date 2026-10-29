using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Logging;
using Oscova.Agents.Infrastructure.Logging;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/logs")]
[IgnoreAntiforgeryToken]
public sealed class LogsController(
    UserServiceManager userServiceManager,
    IUserDirectoryService userDirectories)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet("files")]
    public ActionResult<IReadOnlyList<string>> GetFiles(
        [FromQuery] string scope = "self",
        [FromQuery] string? targetUserId = null)
    {
        if (!TryResolveRoot(scope, targetUserId, out var root, out var prefix))
        {
            return Forbid();
        }

        return Ok(LogReaderService.GetLogFiles(root, prefix));
    }

    [HttpGet]
    public async Task<ActionResult<LogPage>> GetPage(
        [FromQuery] string scope = "self",
        [FromQuery] string? targetUserId = null,
        [FromQuery] string? fileName = null,
        [FromQuery] string? level = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        if (!TryResolveRoot(scope, targetUserId, out var root, out var prefix))
        {
            return Forbid();
        }

        if (!string.IsNullOrWhiteSpace(fileName) && !IsKnownLogFile(root, prefix, fileName))
        {
            return BadRequest(new ErrorResult { Message = "Invalid log file name." });
        }

        var result = await LogReaderService.ReadLogsAsync(
            root, prefix, fileName, level, searchTerm,
            Math.Max(1, page), Math.Clamp(pageSize, 1, 500));
        return Ok(result);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string fileName,
        [FromQuery] string? format = "jsonl",
        [FromQuery] string? level = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] string scope = "self",
        [FromQuery] string? targetUserId = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new ErrorResult { Message = "A log file name is required." });
        }

        if (!TryResolveRoot(scope, targetUserId, out var root, out var prefix))
        {
            return Forbid();
        }

        if (!IsKnownLogFile(root, prefix, fileName))
        {
            return BadRequest(new ErrorResult { Message = "Invalid log file name." });
        }

        var normalizedFormat = format?.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("jsonl" or "txt" or "csv"))
        {
            return BadRequest(new ErrorResult { Message = "Unsupported log export format." });
        }

        ApplyPrivateFileHeaders();
        if (normalizedFormat == "jsonl")
        {
            // Bypass parsing so backup-before-clear preserves every byte.
            return new PhysicalFileResult(Path.Combine(root, fileName), "application/x-ndjson")
            {
                FileDownloadName = fileName
            };
        }

        var page = await LogReaderService.ReadLogsAsync(
            root, prefix, fileName, level, searchTerm, 1, int.MaxValue).ConfigureAwait(false);
        var content = normalizedFormat == "txt"
            ? FormatText(page.Entries)
            : FormatCsv(page.Entries);
        var downloadName = $"{Path.GetFileNameWithoutExtension(fileName)}-filtered.{normalizedFormat}";
        var contentType = normalizedFormat == "txt"
            ? "text/plain; charset=utf-8"
            : "text/csv; charset=utf-8";
        return File(Encoding.UTF8.GetBytes(content), contentType, downloadName);
    }

    [HttpDelete]
    public async Task<IActionResult> Clear(
        [FromQuery] string fileName,
        [FromQuery] string scope = "self",
        [FromQuery] string? targetUserId = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new ErrorResult { Message = "A log file name is required." });
        }

        if (!TryResolveRoot(scope, targetUserId, out var root, out var prefix))
        {
            return Forbid();
        }

        if (!IsKnownLogFile(root, prefix, fileName))
        {
            return BadRequest(new ErrorResult { Message = "Invalid log file name." });
        }

        await LogReaderService.ClearLogFileAsync(root, fileName);
        return NoContent();
    }

    // Reject rooted paths, traversal segments, or any file not in the enumerated log set for this scope.
    private static bool IsKnownLogFile(string root, string prefix, string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || Path.IsPathRooted(fileName))
        {
            return false;
        }

        return LogReaderService.GetLogFiles(root, prefix).Contains(fileName, StringComparer.Ordinal);
    }

    private static string FormatText(IReadOnlyList<LogEntry> entries)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                builder.AppendLine();
            }

            var entry = entries[index];
            builder.Append(entry.Time)
                .Append(" [").Append(entry.Level).Append("] ")
                .Append(entry.Logger).Append(": ")
                .AppendLine(IndentContinuation(entry.Message));

            foreach (var pair in entry.GetMetadataDisplayPairs())
            {
                builder.Append("  metadata.").Append(pair.Key).Append(": ")
                    .AppendLine(IndentContinuation(pair.Value));
            }

            if (entry.Exception is not null)
            {
                builder.Append("  exception: ")
                    .AppendLine(IndentContinuation($"{entry.Exception.Type}: {entry.Exception.Message}"));
                if (!string.IsNullOrWhiteSpace(entry.Exception.Stacktrace))
                {
                    builder.Append("  stacktrace: ")
                        .AppendLine(IndentContinuation(entry.Exception.Stacktrace));
                }
            }
        }

        return builder.ToString();
    }

    private static string FormatCsv(IReadOnlyList<LogEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"time\",\"level\",\"logger\",\"message\",\"metadata\",\"exceptionType\",\"exceptionMessage\",\"stacktrace\"");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(',',
            [
                QuoteCsv(entry.Time),
                QuoteCsv(entry.Level),
                QuoteCsv(entry.Logger),
                QuoteCsv(entry.Message),
                QuoteCsv(entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata)),
                QuoteCsv(entry.Exception?.Type),
                QuoteCsv(entry.Exception?.Message),
                QuoteCsv(entry.Exception?.Stacktrace)
            ]));
        }

        return builder.ToString();
    }

    private static string IndentContinuation(string value) =>
        value.ReplaceLineEndings(Environment.NewLine + "  ");

    private static string QuoteCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
        {
            // Prevent spreadsheet applications from evaluating log-controlled text as a formula.
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private bool TryResolveRoot(string scope, string? targetUserId, out string root, out string prefix)
    {
        var isAdmin = User.IsInRole("Admin");
        switch (scope)
        {
            case "self":
                root = userDirectories.GetLogsDirectoryPath(GetUserId());
                prefix = "user";
                return true;

            case "system" when isAdmin:
                root = PathUtil.SystemLogsPath;
                prefix = "system";
                return true;

            case "user" when isAdmin && Guid.TryParse(targetUserId, out _):
                root = userDirectories.GetLogsDirectoryPath(targetUserId);
                prefix = "user";
                return true;

            default:
                root = string.Empty;
                prefix = string.Empty;
                return false;
        }
    }
}
