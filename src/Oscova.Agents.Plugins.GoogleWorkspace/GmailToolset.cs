using System.Buffers.Text;
using System.ComponentModel;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>Exposes Gmail reads and sends through the connected Google account.</summary>
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
            using var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            using var transport = service?.HttpClient.MessageHandler;
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
            using var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            using var transport = service?.HttpClient.MessageHandler;
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

    /// <summary>Sends an email from the connected Gmail account.</summary>
    [DisplayName("send_email")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [AgentTool]
    [Description("""
        Sends an email from the connected Gmail account.
        Provide one or more recipients in the to field. Separate multiple addresses with commas.
        Optional cc recipients may also be comma separated.
        """)]
    public async Task<string> SendEmailAsync(
        [Description("One or more recipient email addresses separated by commas.")] string to,
        [Description("The email subject.")] string subject,
        [Description("The email body content.")] string body,
        [Description("Optional cc recipient email addresses separated by commas.")] string? cc = null,
        [Description("When true, send the body as HTML. Defaults to false for plain text.")] bool isHtml = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new ToolFailureException("At least one email recipient is required.");
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ToolFailureException("An email subject is required.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ToolFailureException("An email body is required.");
        }

        var toRecipients = ParseRecipients(to);
        if (toRecipients.Count == 0)
        {
            throw new ToolFailureException("At least one email recipient is required.");
        }

        var ccRecipients = ParseRecipients(cc);
        // Header values are written raw into the MIME message, so line breaks must never reach them.
        var cleanSubject = string.Join(' ', subject.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var mime = new StringBuilder()
            .Append("To: ").Append(string.Join(", ", toRecipients)).Append("\r\n");
        if (ccRecipients.Count > 0)
        {
            mime.Append("Cc: ").Append(string.Join(", ", ccRecipients)).Append("\r\n");
        }

        mime.Append("Subject: ").Append(EncodeHeaderWords(cleanSubject)).Append("\r\n")
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: ").Append(isHtml ? "text/html" : "text/plain").Append("; charset=UTF-8\r\n")
            .Append("Content-Transfer-Encoding: base64\r\n\r\n")
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(body), Base64FormattingOptions.InsertLineBreaks));

        return await GoogleWorkspaceExecution.ExecuteAsync(async () =>
        {
            using var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            using var transport = service?.HttpClient.MessageHandler;
            if (service is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var message = new Message { Raw = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(mime.ToString())) };
            await service.Users.Messages.Send(message, "me").ExecuteAsync(ct).ConfigureAwait(false);

            var builder = new StringBuilder();
            builder.AppendLine("Email sent.")
                .AppendLine($"To: {string.Join(", ", toRecipients)}")
                .AppendLine($"Subject: {cleanSubject}");

            if (ccRecipients.Count > 0)
            {
                builder.AppendLine($"Cc: {string.Join(", ", ccRecipients)}");
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Gmail", NotConnectedMessage).ConfigureAwait(false);
    }

    // Encoded words may not exceed 75 characters, so the value folds into 45-byte chunks that never split a character.
    private static string EncodeHeaderWords(string value)
    {
        var words = new List<string>();
        var chunk = new List<byte>();
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            var length = rune.EncodeToUtf8(buffer);
            if (chunk.Count + length > 45)
            {
                words.Add($"=?UTF-8?B?{Convert.ToBase64String([.. chunk])}?=");
                chunk.Clear();
            }

            chunk.AddRange(buffer[..length]);
        }

        words.Add($"=?UTF-8?B?{Convert.ToBase64String([.. chunk])}?=");
        return string.Join("\r\n ", words);
    }

    // Each address must parse on its own, which also rejects CR/LF header injection.
    private static List<string> ParseRecipients(string? recipients)
    {
        if (string.IsNullOrWhiteSpace(recipients))
        {
            return [];
        }

        var addresses = new List<string>();
        foreach (var entry in recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailAddress.TryCreate(entry, out var parsed) || entry.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ToolFailureException($"'{entry}' is not a valid email address.", "Use plain addresses separated by commas.");
            }

            if (!addresses.Contains(parsed.Address, StringComparer.OrdinalIgnoreCase))
            {
                addresses.Add(parsed.Address);
            }
        }

        return addresses;
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
