using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Xml;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>
/// Exposes read-only YouTube tools backed by the shared Google OAuth token service.
/// </summary>
public sealed class YouTubeToolset : IAgentToolset
{
    private const string NotConnectedMessage = "YouTube is not connected. Ask the user to connect their Google account.";

    private readonly AgentPluginContext _context;
    private readonly ILogger<YouTubeToolset> _logger;

    /// <summary>Creates YouTube tools with caller-owned account access and time zone.</summary>
    public YouTubeToolset(AgentPluginContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<YouTubeToolset>();
    }

    /// <summary>
    /// Lists the user's subscribed YouTube channels and returns the channel IDs that can be reused in later tool calls.
    /// </summary>
    [DisplayName("list_subscriptions")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Lists YouTube channels the user is subscribed to.
        Returns the subscribed channel title, channel ID, subscription date, and description snippet.
        Use the returned channel IDs to narrow later requests or to inspect recent uploads from specific channels.
        """)]
    public async Task<string> ListSubscriptionsAsync(
        [Description("Optional text filter applied to channel title and description after the subscriptions are fetched.")] string? query = null,
        [Description("Maximum number of subscriptions to return (default 10, max 50).")] int maxResults = 10,
        [Description("Continuation token returned by a previous list_subscriptions call. Leave empty for the first page.")] string? pageToken = null,
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

            maxResults = Math.Clamp(maxResults, 1, 50);

            var request = service.Subscriptions.List("snippet");
            request.Mine = true;
            request.MaxResults = maxResults;

            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                request.PageToken = pageToken;
            }

            var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
            var subscriptions = response.Items?
                .Where(subscription => MatchesQuery(subscription, query))
                .ToList();

            if (subscriptions is null || subscriptions.Count == 0)
            {
                var emptyResultBuilder = new StringBuilder();
                emptyResultBuilder.AppendLine("No subscriptions found for the requested page and filter.");

                if (!string.IsNullOrWhiteSpace(query))
                {
                    emptyResultBuilder.AppendLine($"Filter: {query.Trim()}");
                }

                // Preserve the continuation token so callers can keep paging after a locally filtered empty page.
                if (!string.IsNullOrWhiteSpace(response.NextPageToken))
                {
                    emptyResultBuilder.AppendLine($"Next page token: {response.NextPageToken}");
                }

                return emptyResultBuilder.ToString().TrimEnd();
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {subscriptions.Count} subscription(s):");

            if (!string.IsNullOrWhiteSpace(query))
            {
                builder.AppendLine($"Filter: {query.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(response.NextPageToken))
            {
                builder.AppendLine($"Next page token: {response.NextPageToken}");
            }

            builder.AppendLine();

            for (var index = 0; index < subscriptions.Count; index++)
            {
                var subscription = subscriptions[index];
                var snippet = subscription.Snippet;
                var channelId = snippet?.ResourceId?.ChannelId;

                builder.AppendLine($"[{index + 1}] Channel ID: {channelId ?? "(unknown)"}")
                    .AppendLine($"    Channel: {snippet?.Title ?? "(no title)"}");

                var subscribedAt = TryParseYouTubeDate(snippet?.PublishedAtRaw);
                if (subscribedAt is { } subscribedAtValue)
                {
                    builder.AppendLine($"    Subscribed: {FormatUserDateTime(subscribedAtValue)}");
                }

                var summary = ToSingleLine(snippet?.Description, 220);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    builder.AppendLine($"    About: {summary}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "YouTube", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks recent uploads from the user's subscribed channels using uploads playlists to keep quota usage low.
    /// </summary>
    [DisplayName("check_new_uploads")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Checks recent uploads from the user's subscribed YouTube channels.
        Uses each channel's uploads playlist instead of broad search so quota usage stays low.
        Returns the newest matching videos with title, channel, publish time, URL, and a short description snippet.
        """)]
    public async Task<string> CheckNewUploadsAsync(
        [Description("How far back to look for uploads, in hours (default 24, max 720).")] int hoursBack = 24,
        [Description("Optional filter applied to subscription channel title and description before checking uploads.")] string? channelQuery = null,
        [Description("Maximum number of subscribed channels to inspect (default 25, max 50).")] int maxChannels = 25,
        [Description("Maximum number of videos to return in the final result (default 20, max 50).")] int maxVideos = 20,
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

            hoursBack = Math.Clamp(hoursBack, 1, 720);
            maxChannels = Math.Clamp(maxChannels, 1, 50);
            maxVideos = Math.Clamp(maxVideos, 1, 50);

            var subscriptions = await GetSubscriptionsAsync(service, maxChannels, channelQuery, ct).ConfigureAwait(false);
            if (subscriptions.Count == 0)
            {
                return "No subscribed channels matched the requested filter.";
            }

            var uploadsPlaylists = await GetUploadsPlaylistsAsync(service, subscriptions, ct).ConfigureAwait(false);
            if (uploadsPlaylists.Count == 0)
            {
                return "No uploads playlists were found for the selected subscriptions.";
            }

            var cutoff = DateTimeOffset.UtcNow.AddHours(-hoursBack);
            var videos = new List<RecentUpload>();

            foreach (var playlist in uploadsPlaylists)
            {
                // Uploads playlists are already newest-first, so a small page is usually enough.
                var request = service.PlaylistItems.List("snippet,contentDetails");
                request.PlaylistId = playlist.UploadsPlaylistId;
                request.MaxResults = 10;

                var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
                if (response.Items is null)
                {
                    continue;
                }

                foreach (var item in response.Items)
                {
                    var videoId = item.ContentDetails?.VideoId ?? item.Snippet?.ResourceId?.VideoId;
                    if (string.IsNullOrWhiteSpace(videoId))
                    {
                        continue;
                    }

                    // Uploads playlists are newest-first, so once a dated item is older than the cutoff
                    // the remaining items in this playlist page will also be too old.
                    var publishedAt = TryParseYouTubeDate(item.ContentDetails?.VideoPublishedAtRaw)
                        ?? TryParseYouTubeDate(item.Snippet?.PublishedAtRaw);
                    if (publishedAt is null)
                    {
                        continue;
                    }

                    if (publishedAt.Value < cutoff)
                    {
                        break;
                    }

                    videos.Add(new RecentUpload(
                        videoId,
                        item.Snippet?.Title ?? "(no title)",
                        playlist.ChannelTitle,
                        publishedAt.Value,
                        item.Snippet?.Description));
                }
            }

            var recentVideos = videos
                .OrderByDescending(video => video.PublishedAt)
                .Take(maxVideos)
                .ToList();

            if (recentVideos.Count == 0)
            {
                return $"No uploads found in the last {hoursBack} hour(s).";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Showing {recentVideos.Count} recent upload(s) from {uploadsPlaylists.Count} channel(s):")
                .AppendLine($"Window: last {hoursBack} hour(s)");

            if (!string.IsNullOrWhiteSpace(channelQuery))
            {
                builder.AppendLine($"Channel filter: {channelQuery.Trim()}");
            }

            builder.AppendLine();

            for (var index = 0; index < recentVideos.Count; index++)
            {
                var video = recentVideos[index];
                builder.AppendLine($"[{index + 1}] Video ID: {video.VideoId}")
                    .AppendLine($"    Title: {video.Title}")
                    .AppendLine($"    Channel: {video.ChannelTitle}")
                    .AppendLine($"    Published: {FormatUserDateTime(video.PublishedAt)}")
                    .AppendLine($"    URL: https://www.youtube.com/watch?v={video.VideoId}");

                var summary = ToSingleLine(video.Description, 220);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    builder.AppendLine($"    Summary: {summary}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "YouTube", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the full details for a single YouTube video so the agent can inspect it before recommending or summarizing it.
    /// </summary>
    [DisplayName("get_video_details")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("Gets full details for a specific YouTube video by ID, including title, channel, publish date, duration, URL, and description.")]
    public async Task<string> GetVideoDetailsAsync(
        [Description("The YouTube video ID obtained from check_new_uploads or another trusted source.")] string videoId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            throw new ToolFailureException("A YouTube video ID is required.");
        }

        return await GoogleWorkspaceExecution.ExecuteAsync(async () =>
        {
            using var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            using var transport = service?.HttpClient.MessageHandler;
            if (service is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var request = service.Videos.List("snippet,contentDetails,statistics");
            request.Id = videoId.Trim();

            var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
            var video = response.Items?.FirstOrDefault();
            if (video is null)
            {
                throw new ToolFailureException("The YouTube video was not found.", "Check the video ID and try again.");
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Title: {video.Snippet?.Title ?? "(no title)"}")
                .AppendLine($"Channel: {video.Snippet?.ChannelTitle ?? "(unknown channel)"}");

            var publishedAt = TryParseYouTubeDate(video.Snippet?.PublishedAtRaw);
            if (publishedAt is { } publishedAtValue)
            {
                builder.AppendLine($"Published: {FormatUserDateTime(publishedAtValue)}");
            }

            var duration = FormatDuration(video.ContentDetails?.Duration);
            if (!string.IsNullOrWhiteSpace(duration))
            {
                builder.AppendLine($"Duration: {duration}");
            }

            builder.AppendLine($"URL: https://www.youtube.com/watch?v={video.Id}");

            if (video.Statistics?.ViewCount is not null)
            {
                builder.AppendLine($"Views: {video.Statistics.ViewCount.Value.ToString("N0", CultureInfo.InvariantCulture)}");
            }

            if (video.Statistics?.LikeCount is not null)
            {
                builder.AppendLine($"Likes: {video.Statistics.LikeCount.Value.ToString("N0", CultureInfo.InvariantCulture)}");
            }

            if (!string.IsNullOrWhiteSpace(video.Snippet?.Description))
            {
                builder.AppendLine()
                    .AppendLine("Description:")
                    .AppendLine(video.Snippet.Description);
            }

            return builder.ToString().TrimEnd();
        }, _logger, "YouTube", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a YouTube Data API client for the current user if a valid Google access token is available.
    /// </summary>
    private async Task<YouTubeService?> CreateServiceAsync(CancellationToken ct)
    {
        var initializer = await GoogleWorkspaceClientFactory.CreateInitializerAsync(_context, ct).ConfigureAwait(false);
        return initializer is null ? null : new YouTubeService(initializer);
    }

    /// <summary>
    /// Fetches subscriptions across pages until enough matching channels have been collected.
    /// </summary>
    private static async Task<List<Subscription>> GetSubscriptionsAsync(YouTubeService service, int maxChannels, string? query, CancellationToken ct)
    {
        var subscriptions = new List<Subscription>();
        string? pageToken = null;

        while (subscriptions.Count < maxChannels)
        {
            var request = service.Subscriptions.List("snippet");
            request.Mine = true;
            request.MaxResults = Math.Min(50, maxChannels);
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
            if (response.Items is not null)
            {
                foreach (var subscription in response.Items)
                {
                    if (!MatchesQuery(subscription, query))
                    {
                        continue;
                    }

                    subscriptions.Add(subscription);
                    if (subscriptions.Count >= maxChannels)
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(response.NextPageToken))
            {
                break;
            }

            pageToken = response.NextPageToken;
        }

        return subscriptions;
    }

    /// <summary>
    /// Resolves each subscribed channel to its uploads playlist so recent videos can be checked cheaply.
    /// </summary>
    private static async Task<List<UploadsPlaylist>> GetUploadsPlaylistsAsync(YouTubeService service, IReadOnlyList<Subscription> subscriptions, CancellationToken ct)
    {
        var results = new List<UploadsPlaylist>();
        var channelInfo = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var subscription in subscriptions)
        {
            var channelId = subscription.Snippet?.ResourceId?.ChannelId;
            if (string.IsNullOrWhiteSpace(channelId) || channelInfo.ContainsKey(channelId))
            {
                continue;
            }

            channelInfo[channelId] = subscription.Snippet?.Title ?? channelId;
        }

        foreach (var batch in channelInfo.Keys.Chunk(50))
        {
            var request = service.Channels.List("contentDetails");
            request.Id = string.Join(",", batch);

            var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
            if (response.Items is null)
            {
                continue;
            }

            foreach (var channel in response.Items)
            {
                var channelId = channel.Id;
                var uploadsPlaylistId = channel.ContentDetails?.RelatedPlaylists?.Uploads;
                if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(uploadsPlaylistId))
                {
                    continue;
                }

                channelInfo.TryGetValue(channelId, out var channelTitle);
                results.Add(new UploadsPlaylist(channelId, channelTitle ?? channelId, uploadsPlaylistId));
            }
        }

        return results;
    }

    /// <summary>
    /// Applies a simple case-insensitive text match against the subscription's visible metadata.
    /// </summary>
    private static bool MatchesQuery(Subscription subscription, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var value = query.Trim();
        var title = subscription.Snippet?.Title ?? string.Empty;
        var description = subscription.Snippet?.Description ?? string.Empty;

        return title.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               description.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts multi-line text into a short single-line summary that fits tool output better.
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
    /// Formats the ISO 8601 YouTube duration string into a human-readable duration when possible.
    /// </summary>
    private static string? FormatDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return null;
        }

        try
        {
            var timeSpan = XmlConvert.ToTimeSpan(duration);
            return timeSpan.TotalHours >= 1
                ? $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}"
                : $"{timeSpan.Minutes}:{timeSpan.Seconds:D2}";
        }
        catch
        {
            return duration;
        }
    }

    private string FormatUserDateTime(DateTimeOffset value)
    {
        return TimeZoneInfo.ConvertTime(value, _context.UserTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses YouTube date strings defensively because some generated DateTime helper accessors throw on malformed values.
    /// </summary>
    private static DateTimeOffset? TryParseYouTubeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;
    }

    private sealed record UploadsPlaylist(string ChannelId, string ChannelTitle, string UploadsPlaylistId);

    private sealed record RecentUpload(
        string VideoId,
        string Title,
        string ChannelTitle,
        DateTimeOffset PublishedAt,
        string? Description);
}
