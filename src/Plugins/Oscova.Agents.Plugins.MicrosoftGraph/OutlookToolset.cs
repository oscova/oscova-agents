using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Me.SendMail;
using Microsoft.Graph.Models;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.MicrosoftGraph;

/// <summary>
/// Exposes Outlook mail tools backed by Microsoft Graph and a scoped account token.
/// </summary>
public sealed partial class OutlookToolset : IAgentToolset
{
    private const string NotConnectedMessage = "Outlook is not connected. Ask the user to connect their Microsoft account through the host connected accounts setup.";

    private readonly AgentPluginContext _context;
    private readonly ILogger<OutlookToolset> _logger;

    /// <summary>Creates the per-user tools with host-provided account and time-zone services.</summary>
    public OutlookToolset(AgentPluginContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<OutlookToolset>();
    }

    /// <summary>
    /// Lists recent Outlook emails from a mailbox folder so the agent can inspect candidate message IDs.
    /// </summary>
    [DisplayName("list_emails")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Lists recent Outlook emails from a mailbox folder.
        Returns message IDs, sender, subject, received time, read status, attachment presence, and a preview snippet.
        Use read_email with a returned message ID to inspect the full email body.
        Well-known folders include: inbox, drafts, sentitems, archive, deleteditems, junkemail.
        """)]
    public async Task<string> ListEmailsAsync(
        [Description("Mailbox folder name or folder ID. Defaults to inbox.")] string folder = "inbox",
        [Description("Maximum number of emails to return (default 10, max 25).")]
        int maxResults = 10,
        [Description("When true, only unread emails are returned.")] bool unreadOnly = false,
        CancellationToken ct = default)
    {
        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            maxResults = Math.Clamp(maxResults, 1, 25);

            using var transport = _context.CreateHttpClient();
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, transport, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var folderId = ResolveFolderIdentifier(folder);
            var response = await client.Me.MailFolders[folderId].Messages.GetAsync(request =>
            {
                request.QueryParameters.Top = maxResults;
                request.QueryParameters.Orderby = ["receivedDateTime DESC"];
                request.QueryParameters.Select =
                [
                    "id",
                    "from",
                    "subject",
                    "receivedDateTime",
                    "isRead",
                    "hasAttachments",
                    "bodyPreview",
                    "importance"
                ];

                // Keep the first version simple: only unread filtering is exposed here.
                if (unreadOnly)
                {
                    request.QueryParameters.Filter = "isRead eq false";
                }
            }, cancellationToken: ct).ConfigureAwait(false);

            var messages = response?.Value;
            if (messages is null || messages.Count == 0)
            {
                return "No Outlook emails were found for that folder.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {messages.Count} email(s):")
                .AppendLine($"Folder: {folderId}");

            if (unreadOnly)
            {
                builder.AppendLine("Filter: unread only");
            }

            builder.AppendLine();

            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                builder.AppendLine($"[{index + 1}] ID: {message.Id ?? "(unknown)"}")
                    .AppendLine($"    From: {FormatSender(message.From) ?? "(unknown sender)"}")
                    .AppendLine($"    Subject: {message.Subject ?? "(no subject)"}")
                    .AppendLine($"    Status: {(message.IsRead == true ? "read" : "unread")}");

                var received = FormatMessageDate(message.ReceivedDateTime);
                if (!string.IsNullOrWhiteSpace(received))
                {
                    builder.AppendLine($"    Received: {received}");
                }

                var importance = message.Importance?.ToString()?.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(importance))
                {
                    builder.AppendLine($"    Importance: {importance}");
                }
                if (message.HasAttachments == true)
                {
                    builder.AppendLine("    Attachments: yes");
                }

                if (!string.IsNullOrWhiteSpace(message.BodyPreview))
                {
                    builder.AppendLine($"    Preview: {ToSingleLine(message.BodyPreview, 220)}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Outlook", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a specific Outlook email in detail by its message ID.
    /// </summary>
    [DisplayName("read_email")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("Reads a specific Outlook email by message ID. Use list_emails or search_emails first to get message IDs.")]
    public async Task<string> ReadEmailAsync(
        [Description("The Outlook message ID obtained from list_emails or search_emails.")] string messageId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ToolFailureException("An Outlook message ID is required.", "Use an ID returned by an Outlook list or search.");
        }

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            using var transport = _context.CreateHttpClient();
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, transport, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var message = await client.Me.Messages[messageId.Trim()].GetAsync(request =>
            {
                request.QueryParameters.Select =
                [
                    "id",
                    "from",
                    "toRecipients",
                    "ccRecipients",
                    "subject",
                    "receivedDateTime",
                    "body",
                    "hasAttachments",
                    "importance",
                    "isRead"
                ];
            }, cancellationToken: ct).ConfigureAwait(false);

            if (message is null)
            {
                throw new ToolFailureException("The Outlook email could not be found.", "Refresh the email list and try again.");
            }

            var builder = new StringBuilder();
            builder.AppendLine($"From: {FormatSender(message.From) ?? "(unknown sender)"}");

            var toRecipients = FormatRecipients(message.ToRecipients);
            if (!string.IsNullOrWhiteSpace(toRecipients))
            {
                builder.AppendLine($"To: {toRecipients}");
            }

            var ccRecipients = FormatRecipients(message.CcRecipients);
            if (!string.IsNullOrWhiteSpace(ccRecipients))
            {
                builder.AppendLine($"Cc: {ccRecipients}");
            }

            builder.AppendLine($"Subject: {message.Subject ?? "(no subject)"}")
                .AppendLine($"Status: {(message.IsRead == true ? "read" : "unread")}");

            var received = FormatMessageDate(message.ReceivedDateTime);
            if (!string.IsNullOrWhiteSpace(received))
            {
                builder.AppendLine($"Received: {received}");
            }

            var importance = message.Importance?.ToString()?.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(importance))
            {
                builder.AppendLine($"Importance: {importance}");
            }
            builder.AppendLine($"Attachments: {(message.HasAttachments == true ? "yes" : "no")}")
                .AppendLine()
                .AppendLine(ToolOutput.LimitText(_context.Services.ToolOutputStore, "email", GetMessageBody(message)));

            return builder.ToString().TrimEnd();
        }, _logger, "Outlook", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches Outlook emails across the mailbox using Microsoft Graph message search.
    /// </summary>
    [DisplayName("search_emails")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Searches Outlook emails across the mailbox.
        Returns compact message results with IDs, sender, subject, received time, and preview snippets.
        Use read_email with a returned message ID to inspect the full message body.
        """)]
    public async Task<string> SearchEmailsAsync(
        [Description("Search text for Outlook email search.")] string query,
        [Description("Maximum number of emails to return (default 10, max 25).")]
        int maxResults = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolFailureException("An Outlook search query is required.");
        }

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            maxResults = Math.Clamp(maxResults, 1, 25);
            var trimmedQuery = query.Trim();
            var escapedQuery = trimmedQuery.Replace("\"", "\\\"", StringComparison.Ordinal);

            using var transport = _context.CreateHttpClient();
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, transport, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var response = await client.Me.Messages.GetAsync(request =>
            {
                request.QueryParameters.Top = maxResults;
                request.QueryParameters.Search = $"\"{escapedQuery}\"";
                // Graph mail search requires eventual consistency headers for reliable $search execution.
                request.Headers.Add("ConsistencyLevel", "eventual");
                request.QueryParameters.Select =
                [
                    "id",
                    "from",
                    "subject",
                    "receivedDateTime",
                    "isRead",
                    "hasAttachments",
                    "bodyPreview",
                    "importance"
                ];
            }, cancellationToken: ct).ConfigureAwait(false);

            var messages = response?.Value;
            if (messages is null || messages.Count == 0)
            {
                return "No Outlook emails matched that search.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {messages.Count} email(s):")
                .AppendLine($"Query: {trimmedQuery}")
                .AppendLine();

            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                builder.AppendLine($"[{index + 1}] ID: {message.Id ?? "(unknown)"}")
                    .AppendLine($"    From: {FormatSender(message.From) ?? "(unknown sender)"}")
                    .AppendLine($"    Subject: {message.Subject ?? "(no subject)"}")
                    .AppendLine($"    Status: {(message.IsRead == true ? "read" : "unread")}");

                var received = FormatMessageDate(message.ReceivedDateTime);
                if (!string.IsNullOrWhiteSpace(received))
                {
                    builder.AppendLine($"    Received: {received}");
                }

                var importance = message.Importance?.ToString()?.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(importance))
                {
                    builder.AppendLine($"    Importance: {importance}");
                }
                if (message.HasAttachments == true)
                {
                    builder.AppendLine("    Attachments: yes");
                }

                if (!string.IsNullOrWhiteSpace(message.BodyPreview))
                {
                    builder.AppendLine($"    Preview: {ToSingleLine(message.BodyPreview, 220)}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Outlook", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an Outlook email from the connected Microsoft account.
    /// </summary>
    [DisplayName("send_email")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [AgentTool]
    [Description("""
        Sends an Outlook email from the connected Microsoft account.
        Provide one or more recipients in the to field. Separate multiple addresses with commas.
        Optional cc recipients may also be comma separated.
        """)]
    public async Task<string> SendEmailAsync(
        [Description("One or more recipient email addresses separated by commas.")] string to,
        [Description("The email subject.")] string subject,
        [Description("The email body content.")] string body,
        [Description("Optional cc recipient email addresses separated by commas.")] string? cc = null,
        [Description("When true, save the message in Sent Items. Defaults to true.")] bool saveToSentItems = true,
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
            throw new ToolFailureException("At least one valid email recipient is required.");
        }

        var ccRecipients = ParseRecipients(cc);

        return await MicrosoftGraphExecution.ExecuteAsync(async () =>
        {
            using var transport = _context.CreateHttpClient();
            using var client = await MicrosoftGraphClientFactory.CreateAsync(_context, transport, ct).ConfigureAwait(false);
            if (client is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var requestBody = new SendMailPostRequestBody
            {
                Message = new Message
                {
                    Subject = subject.Trim(),
                    Body = new ItemBody
                    {
                        ContentType = isHtml ? BodyType.Html : BodyType.Text,
                        Content = body
                    },
                    ToRecipients = toRecipients,
                    CcRecipients = ccRecipients.Count > 0 ? ccRecipients : null
                },
                SaveToSentItems = saveToSentItems
            };

            await client.Me.SendMail.PostAsync(requestBody, cancellationToken: ct).ConfigureAwait(false);

            var builder = new StringBuilder();
            builder.AppendLine("Email sent.")
                .AppendLine($"To: {FormatRecipients(toRecipients)}")
                .AppendLine($"Subject: {subject.Trim()}");

            if (ccRecipients.Count > 0)
            {
                builder.AppendLine($"Cc: {FormatRecipients(ccRecipients)}");
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Outlook", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a friendly Outlook folder name to the Graph well-known folder identifier.
    /// </summary>
    private static string ResolveFolderIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "inbox";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "inbox" => "inbox",
            "drafts" => "drafts",
            "sent" or "sentitems" => "sentitems",
            "archive" => "archive",
            "deleted" or "deleteditems" => "deleteditems",
            "junk" or "junkemail" => "junkemail",
            _ => value.Trim()
        };
    }

    /// <summary>
    /// Formats the sender display name for compact tool output.
    /// </summary>
    private static string? FormatSender(Recipient? value)
    {
        var address = value?.EmailAddress?.Address;
        var name = value?.EmailAddress?.Name;

        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(name)
            ? address
            : $"{name} <{address}>";
    }

    /// <summary>
    /// Formats a recipient collection into a compact comma-separated string.
    /// </summary>
    private static string? FormatRecipients(List<Recipient>? recipients)
    {
        if (recipients is null || recipients.Count == 0)
        {
            return null;
        }

        var formattedRecipients = recipients
            .Select(FormatSender)
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
            .Cast<string>()
            .ToList();

        return formattedRecipients.Count == 0
            ? null
            : string.Join(", ", formattedRecipients);
    }

    /// <summary>
    /// Parses comma-separated email addresses into Graph recipients.
    /// </summary>
    private static List<Recipient> ParseRecipients(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(address => new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Address = address
                }
            })];
    }

    /// <summary>
    /// Formats Graph message timestamps into readable user-local output.
    /// </summary>
    private string? FormatMessageDate(DateTimeOffset? value)
    {
        return value is null ? null : TimeZoneInfo.ConvertTime(value.Value, _context.UserTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns a readable message body, stripping HTML tags when needed.
    /// </summary>
    private static string GetMessageBody(Message message)
    {
        var content = message.Body?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return "(no body content)";
        }

        if (message.Body?.ContentType == BodyType.Html)
        {
            var cleaned = StripHtml(content);
            return string.IsNullOrWhiteSpace(cleaned) ? "(no body content)" : cleaned;
        }

        return content.Trim();
    }

    /// <summary>
    /// Converts multi-line text into a single concise line for compact tool output.
    /// </summary>
    private static string? ToSingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        return $"{collapsed[..maxLength].TrimEnd()}...";
    }

    /// <summary>
    /// Removes the most common HTML tags from Outlook email body content.
    /// </summary>
    private static string? StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WebUtility.HtmlDecode(withoutTags)?.Trim();
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled, 100)]
    private static partial Regex HtmlTagRegex();

}
