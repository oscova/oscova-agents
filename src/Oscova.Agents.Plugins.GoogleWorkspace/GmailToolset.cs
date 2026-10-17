using System.ComponentModel;
using System.Globalization;
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>Exposes Gmail reads through the connected Google account.</summary>
public sealed class GmailToolset : IAgentToolset
{
    private const string NotConnectedMessage = "Gmail is not connected. Ask the user to connect their Google account.";

    private readonly AgentPluginContext _context;
    private readonly ILogger<GmailToolset> _logger;

    /// <summary>Creates Gmail tools with caller-owned account access and time zone.</summary>
    public GmailToolset(AgentPluginContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<GmailToolset>();
    }

    /// <summary>Searches messages in the connected Gmail account.</summary>
    [DisplayName("search_emails")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Searches Gmail messages and returns a concise list of candidate emails with message IDs, sender, subject, date, and preview snippet.
        Use Gmail search syntax in the query parameter.
        Use read_email with a returned message ID to inspect the full message body.
        """)]
    public async Task<string> SearchEmailsAsync(
        [Description("Gmail search query using Gmail search syntax. Leave empty to list recent messages from the chosen labels.")] string? query = null,
        [Description("Maximum number of emails to return (default 10, max 25).")] int maxResults = 10,
        [Description("Optional Gmail label IDs separated by commas. Examples: 'INBOX', 'UNREAD', or 'INBOX,UNREAD'. Leave empty to search all mail except spam/trash.")] string? labelIds = "INBOX",
        CancellationToken ct = default)
    {
        return await GoogleWorkspaceExecution.ExecuteAsync(async () =>
        {
            var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            if (service is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            maxResults = Math.Clamp(maxResults, 1, 25);

            var listRequest = service.Users.Messages.List("me");
            listRequest.MaxResults = maxResults;
            if (!string.IsNullOrWhiteSpace(query))
            {
                listRequest.Q = query.Trim();
            }

            var parsedLabelIds = ParseLabelIds(labelIds);
            if (parsedLabelIds.Count > 0)
            {
                listRequest.LabelIds = parsedLabelIds;
            }

            var listResponse = await listRequest.ExecuteAsync(ct).ConfigureAwait(false);
            if (listResponse.Messages is null || listResponse.Messages.Count == 0)
            {
                return "No emails found.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {listResponse.Messages.Count} email(s):");

            if (!string.IsNullOrWhiteSpace(query))
            {
                builder.AppendLine($"Query: {query.Trim()}");
            }

            if (parsedLabelIds.Count > 0)
            {
                builder.AppendLine($"Labels: {string.Join(", ", parsedLabelIds)}");
            }

            builder.AppendLine();

            for (var index = 0; index < listResponse.Messages.Count; index++)
            {
                var messageRef = listResponse.Messages[index];
                if (string.IsNullOrWhiteSpace(messageRef.Id))
                {
                    continue;
                }

                var getRequest = service.Users.Messages.Get("me", messageRef.Id);
                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                getRequest.MetadataHeaders = new[] { "Subject", "From", "Date" };

                var message = await getRequest.ExecuteAsync(ct).ConfigureAwait(false);
                var subject = GetHeader(message, "Subject") ?? "(no subject)";
                var from = GetHeader(message, "From") ?? "(unknown sender)";
                var date = FormatHeaderDate(GetHeader(message, "Date"));

                builder.AppendLine($"[{index + 1}] ID: {message.Id}")
                    .AppendLine($"    From: {from}")
                    .AppendLine($"    Subject: {subject}");

                if (!string.IsNullOrWhiteSpace(date))
                {
                    builder.AppendLine($"    Date: {date}");
                }

                if (!string.IsNullOrWhiteSpace(message.Snippet))
                {
                    builder.AppendLine($"    Preview: {message.Snippet}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Gmail", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>Reads a Gmail message by ID.</summary>
    [DisplayName("read_email")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("Reads the full content of a specific Gmail email by its ID. Use search_emails first to get email IDs.")]
    public async Task<string> ReadEmailAsync(
        [Description("The Gmail message ID obtained from search_emails.")] string messageId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ToolFailureException("A Gmail message ID is required.", "Use an ID returned by search_emails.");
        }

        return await GoogleWorkspaceExecution.ExecuteAsync(async () =>
        {
            var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            if (service is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var getRequest = service.Users.Messages.Get("me", messageId);
            getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;

            var message = await getRequest.ExecuteAsync(ct).ConfigureAwait(false);
            var subject = GetHeader(message, "Subject") ?? "(no subject)";
            var from = GetHeader(message, "From") ?? "(unknown sender)";
            var date = FormatHeaderDate(GetHeader(message, "Date"));
            var body = ExtractBody(message.Payload);

            var builder = new StringBuilder();
            builder.AppendLine($"From: {from}")
                .AppendLine($"Subject: {subject}");

            if (!string.IsNullOrWhiteSpace(date))
            {
                builder.AppendLine($"Date: {date}");
            }

            builder.AppendLine()
                .AppendLine(string.IsNullOrWhiteSpace(body)
                    ? "(no body content)"
                    : ToolOutput.LimitText(_context.Services.ToolOutputStore, "email", body));

            return builder.ToString().TrimEnd();
        }, _logger, "Gmail", NotConnectedMessage).ConfigureAwait(false);
    }

    private async Task<GmailService?> CreateServiceAsync(CancellationToken ct)
    {
        var initializer = await GoogleWorkspaceClientFactory.CreateInitializerAsync(_context, ct).ConfigureAwait(false);
        return initializer is null ? null : new GmailService(initializer);
    }

    private static string? GetHeader(Message message, string headerName)
    {
        return message.Payload?.Headers?
            .FirstOrDefault(header => string.Equals(header.Name, headerName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private string? FormatHeaderDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? TimeZoneInfo.ConvertTime(parsed, _context.UserTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value;
    }

    // Gmail messages can be nested multipart trees. Prefer plain text when available,
    // then HTML, then fall back to any decodable content so the tool returns something useful.
    private static string ExtractBody(MessagePart? part)
    {
        if (part is null)
        {
            return string.Empty;
        }

        if (string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBody(part.Body?.Data);
        }

        if (part.Parts is { Count: > 0 })
        {
            foreach (var child in part.Parts)
            {
                if (string.Equals(child.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    var text = DecodeBody(child.Body?.Data);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            foreach (var child in part.Parts)
            {
                if (string.Equals(child.MimeType, "text/html", StringComparison.OrdinalIgnoreCase))
                {
                    var html = DecodeBody(child.Body?.Data);
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        return html;
                    }
                }
            }

            foreach (var child in part.Parts)
            {
                var nested = ExtractBody(child);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return DecodeBody(part.Body?.Data);
    }

    // Gmail encodes message bodies using base64url rather than standard base64,
    // so the alphabet and padding need to be normalized before decoding.
    private static string DecodeBody(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return string.Empty;
        }

        try
        {
            var base64 = data.Replace('-', '+').Replace('_', '/');
            var padding = base64.Length % 4;
            if (padding > 0)
            {
                base64 = base64.PadRight(base64.Length + (4 - padding), '=');
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return "(body could not be decoded)";
        }
    }

    private static List<string> ParseLabelIds(string? labelIds)
    {
        if (string.IsNullOrWhiteSpace(labelIds))
        {
            return [];
        }

        return [.. labelIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
