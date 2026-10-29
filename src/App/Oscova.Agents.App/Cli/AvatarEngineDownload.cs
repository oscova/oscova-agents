using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Core.ApiContracts.Updates;

namespace Oscova.Agents.App.Cli;

/// <summary>Downloads and verifies the published Avatar Engine ZIP before installation.</summary>
public sealed class AvatarEngineDownload(IHttpClientFactory httpClientFactory, TimeProvider timeProvider)
{
    internal const string ClientName = "avatar-download";
    private const string DownloadUrl = "https://oscova.ai/api/downloads/avatar-engine";
    private readonly Uri? _testSource = UpdateTestSources.GetAvatarSource();
    private string ReleaseUrl => _testSource?.AbsoluteUri ?? DownloadUrl;
    private const string R2HostSuffix = ".r2.cloudflarestorage.com";
    private const int BufferSize = 81_920;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Downloads the published ZIP to a caller-owned path and returns after verification.</summary>
    public async Task<AvatarReleaseIdentity> DownloadAsync(
        string destinationPath,
        Action<AvatarDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(ClientName);
        var partialPath = destinationPath + ".part";
        ReleaseIdentity? pinnedRelease = null;
        var consecutiveForbidden = 0;
        DeletePartialFile(partialPath);

        try
        {
            while (true)
            {
                try
                {
                    using var redirect = await SendHeadersAsync(
                        client,
                        new HttpRequestMessage(HttpMethod.Get, ReleaseUrl),
                        "The Avatar Engine download service",
                        cancellationToken).ConfigureAwait(false);
                    var (release, signedUrl) = ReadRelease(redirect);
                    if (pinnedRelease?.Matches(release) == false)
                    {
                        throw new CliExitException(1, "The Avatar Engine release changed. Run the installation again.");
                    }

                    pinnedRelease ??= release;
                    var offset = GetPartialLength(partialPath, pinnedRelease.SizeBytes);
                    progress?.Invoke(new AvatarDownloadProgress(offset, pinnedRelease.SizeBytes));
                    if (offset == pinnedRelease.SizeBytes)
                    {
                        await VerifyAndPublishAsync(
                            partialPath,
                            destinationPath,
                            pinnedRelease,
                            cancellationToken).ConfigureAwait(false);
                        return pinnedRelease.ToPublicIdentity();
                    }

                    CheckFreeSpace(destinationPath, pinnedRelease.SizeBytes - offset);
                    await DownloadAttemptAsync(
                        client,
                        signedUrl,
                        pinnedRelease,
                        partialPath,
                        offset,
                        () => consecutiveForbidden = 0,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    progress?.Invoke(new(pinnedRelease.SizeBytes, pinnedRelease.SizeBytes, Message: "Verifying"));
                    await VerifyAndPublishAsync(
                        partialPath,
                        destinationPath,
                        pinnedRelease,
                        cancellationToken).ConfigureAwait(false);
                    return pinnedRelease.ToPublicIdentity();
                }
                catch (RetryableDownloadException exception)
                {
                    if (exception.IsForbidden && ++consecutiveForbidden >= 2)
                    {
                        throw new CliExitException(1, exception.Message);
                    }

                    var received = pinnedRelease is null
                        ? 0
                        : GetPartialLength(partialPath, pinnedRelease.SizeBytes);
                    await WaitBeforeRetryAsync(
                        received,
                        pinnedRelease?.SizeBytes ?? 0,
                        exception,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            DeletePartialFileBestEffort(partialPath);
            throw;
        }
    }

    private async Task DownloadAttemptAsync(
        HttpClient client,
        Uri signedUrl,
        ReleaseIdentity release,
        string partialPath,
        long offset,
        Action responseAccepted,
        Action<AvatarDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The website pins the release identity, while R2 must prove the same object before appending.
        var request = new HttpRequestMessage(HttpMethod.Get, signedUrl);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(release.ETag!));
        }

        using var response = await SendHeadersAsync(
            client,
            request,
            "The Avatar Engine release server",
            cancellationToken).ConfigureAwait(false);
        ValidateReleaseResponse(response, release, offset);
        responseAccepted();

        Stream source;
        try
        {
            source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new RetryableDownloadException("The Avatar Engine download could not start.");
        }

        await using (source.ConfigureAwait(false))
        {
            try
            {
                var mode = File.Exists(partialPath) ? FileMode.Open : FileMode.CreateNew;
                var destination = new FileStream(
                    partialPath,
                    mode,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (destination.ConfigureAwait(false))
                {
                    if (destination.Length != offset)
                    {
                        throw new CliExitException(1, "The incomplete Avatar Engine ZIP changed during download. Run the installation again.");
                    }

                    destination.Seek(offset, SeekOrigin.Begin);
                    await CopyAsync(
                        source,
                        destination,
                        offset,
                        release.SizeBytes,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliExitException(1, "The Avatar Engine ZIP could not be written. Check the destination disk and try again.");
            }
        }
    }

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        long offset,
        long expectedSize,
        Action<AvatarDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        var buffer = new byte[BufferSize];
        var received = offset;
        var lastProgress = Stopwatch.StartNew();

        while (true)
        {
            int count;
            try
            {
                count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested)
            {
                throw new RetryableDownloadException("The Avatar Engine download stopped receiving data for two minutes.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new RetryableDownloadException("The Avatar Engine download was interrupted.");
            }

            if (count == 0)
            {
                break;
            }

            if (count > expectedSize - received)
            {
                throw new CliExitException(1, "The Avatar Engine download exceeded its published size. Run the installation again.");
            }

            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliExitException(1, "The Avatar Engine ZIP could not be written. Check the destination disk and try again.");
            }

            received += count;
            idle.CancelAfter(IdleTimeout);
            if (received == expectedSize || lastProgress.Elapsed >= TimeSpan.FromSeconds(1))
            {
                progress?.Invoke(new AvatarDownloadProgress(received, expectedSize));
                lastProgress.Restart();
            }
        }

        if (received != expectedSize)
        {
            throw new RetryableDownloadException("The Avatar Engine download ended before all bytes arrived.");
        }
    }

    private static async Task VerifyAndPublishAsync(
        string partialPath,
        string destinationPath,
        ReleaseIdentity release,
        CancellationToken cancellationToken)
    {
        byte[] actualHash;
        try
        {
            var file = new FileStream(
                partialPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (file.ConfigureAwait(false))
            {
                actualHash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliExitException(1, "The downloaded Avatar Engine ZIP could not be verified. Check the destination disk and try again.");
        }

        if (!CryptographicOperations.FixedTimeEquals(actualHash, release.Sha256))
        {
            throw new CliExitException(1, "The downloaded Avatar Engine ZIP failed SHA-256 verification. Run the installation again.");
        }

        try
        {
            File.Move(partialPath, destinationPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliExitException(1, "The verified Avatar Engine ZIP could not be published. Check the destination disk and try again.");
        }
    }

    private async Task WaitBeforeRetryAsync(
        long received,
        long total,
        RetryableDownloadException exception,
        Action<AvatarDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var delay = exception.Delay > RetryDelay ? exception.Delay : RetryDelay;
        var started = timeProvider.GetTimestamp();
        while (timeProvider.GetElapsedTime(started) < delay)
        {
            var remaining = delay - timeProvider.GetElapsedTime(started);
            progress?.Invoke(new AvatarDownloadProgress(received, total, remaining, exception.Message));
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(
                remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Invoke(new AvatarDownloadProgress(received, total, TimeSpan.Zero, exception.Message));
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(new AvatarDownloadProgress(received, total, Message: "Reconnecting to the Avatar Engine release."));
    }

    private static async Task<HttpResponseMessage> SendHeadersAsync(
        HttpClient client,
        HttpRequestMessage request,
        string stage,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headerTimeout.CancelAfter(HeaderTimeout);
            try
            {
                return await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    headerTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RetryableDownloadException($"{stage} timed out.");
            }
            catch (HttpRequestException)
            {
                throw new RetryableDownloadException($"{stage} could not be reached.");
            }
        }
    }

    /// <summary>Checks the published identity without requesting its signed archive URL.</summary>
    public async Task<AvatarReleaseIdentity> GetReleaseAsync(CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl);
        using var response = await SendHeadersAsync(client, request,
            "The Avatar Engine download service", cancellationToken).ConfigureAwait(false);
        return ReadRelease(response).Release.ToPublicIdentity();
    }

    private (ReleaseIdentity Release, Uri SignedUrl) ReadRelease(HttpResponseMessage response)
    {
        if (IsRetryableStatus(response.StatusCode))
        {
            throw new RetryableDownloadException(
                $"The Avatar Engine download service returned HTTP {(int)response.StatusCode}.",
                GetRetryDelay(response));
        }

        if (response.StatusCode != HttpStatusCode.Found)
        {
            throw new CliExitException(1, "The Avatar Engine download service returned an unexpected response. Try again later.");
        }

        var location = ReadSingleHeader(response, "Location");
        var version = ReadSingleHeader(response, "X-Oscova-Avatar-Version");
        var platform = ReadSingleHeader(response, "X-Oscova-Avatar-Platform");
        var sizeText = ReadSingleHeader(response, "X-Oscova-Avatar-Size");
        var sha256Text = ReadSingleHeader(response, "X-Oscova-Avatar-Sha256");

        if (!Uri.TryCreate(location, UriKind.Absolute, out var signedUri) ||
            !string.IsNullOrEmpty(signedUri.UserInfo) || !string.IsNullOrEmpty(signedUri.Fragment) ||
            (_testSource is not null
                ? signedUri.Scheme != _testSource.Scheme || signedUri.Host != _testSource.Host || signedUri.Port != _testSource.Port
                : signedUri.Scheme != Uri.UriSchemeHttps || signedUri.Port != 443 || !IsR2Host(signedUri.Host)))
        {
            throw new CliExitException(1, "The Avatar Engine download service returned an invalid release location.");
        }

        if (sizeText.Length == 0 ||
            !sizeText.All(char.IsAsciiDigit) ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var sizeBytes) ||
            !new AvatarReleaseIdentity(version, platform, sizeBytes, sha256Text, "published").IsValid())
        {
            throw new CliExitException(1, "The Avatar Engine download service returned invalid release metadata.");
        }

        return (new ReleaseIdentity(
            signedUri.GetLeftPart(UriPartial.Path),
            version,
            platform,
            sizeBytes,
            Convert.FromHexString(sha256Text)), signedUri);
    }

    private static string ReadSingleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            throw new CliExitException(1, "The Avatar Engine download service returned incomplete release metadata.");
        }

        var entries = values.ToArray();
        if (entries is not [{ Length: > 0 } value])
        {
            throw new CliExitException(1, "The Avatar Engine download service returned invalid release metadata.");
        }

        return value;
    }

    private void ValidateReleaseResponse(
        HttpResponseMessage response,
        ReleaseIdentity release,
        long offset)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new RetryableDownloadException(
                "The Avatar Engine release is unavailable or its permission expired.",
                isForbidden: true);
        }

        if (IsRetryableStatus(response.StatusCode))
        {
            throw new RetryableDownloadException(
                $"The Avatar Engine release server returned HTTP {(int)response.StatusCode}.",
                GetRetryDelay(response));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliExitException(1, "The published Avatar Engine release was not found. Try again later.");
        }

        var expectedStatus = offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
        if (response.StatusCode != expectedStatus || response.Content.Headers.ContentEncoding.Count > 0)
        {
            var message = offset == 0
                ? "The Avatar Engine release server returned an unexpected response. Try again later."
                : "The Avatar Engine release server did not accept the saved download position. Run the installation again.";
            throw new CliExitException(1, message);
        }

        var etag = response.Headers.ETag;
        if (etag?.IsWeak != false)
        {
            throw new CliExitException(1, "The Avatar Engine release server did not provide a strong object identifier. Try again later.");
        }

        if (release.ETag is null)
        {
            release.ETag = etag.ToString();
        }
        else if (!string.Equals(release.ETag, etag.ToString(), StringComparison.Ordinal))
        {
            throw new CliExitException(1, "The Avatar Engine release changed while downloading. Run the installation again.");
        }

        var remaining = release.SizeBytes - offset;
        if (response.Content.Headers.Contains("Content-Length") &&
            response.Content.Headers.ContentLength != remaining)
        {
            throw new CliExitException(1, "The Avatar Engine release length does not match its published size. Run the installation again.");
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (offset == 0)
        {
            if (response.Content.Headers.Contains("Content-Range"))
            {
                throw new CliExitException(1, "The Avatar Engine release server returned an unexpected byte range. Try again later.");
            }

            return;
        }

        if (contentRange is null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            contentRange.From != offset ||
            contentRange.To != release.SizeBytes - 1 ||
            contentRange.Length != release.SizeBytes)
        {
            throw new CliExitException(1, "The Avatar Engine release server returned an invalid byte range. Run the installation again.");
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
        {
            return RetryDelay;
        }

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > RetryDelay)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - timeProvider.GetUtcNow();
            if (delay > RetryDelay)
            {
                return delay;
            }
        }

        return RetryDelay;
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool IsR2Host(string host)
    {
        // Path-style signing fixes the R2 redirect host shape accepted here.
        if (!host.EndsWith(R2HostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var accountId = host[..^R2HostSuffix.Length];
        return accountId.Length == 32 && accountId.All(IsAsciiHexCharacter);
    }

    private static bool IsAsciiHexCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static long GetPartialLength(string partialPath, long expectedSize)
    {
        try
        {
            var length = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (length < 0 || length > expectedSize)
            {
                throw new CliExitException(1, "The incomplete Avatar Engine ZIP has an invalid size. Run the installation again.");
            }

            return length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliExitException(1, "The incomplete Avatar Engine ZIP could not be inspected. Check the destination disk and try again.");
        }
    }

    private static void CheckFreeSpace(string destinationPath, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < requiredBytes)
            {
                throw new CliExitException(1, "There is not enough disk space to download the Avatar Engine ZIP.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The write path remains authoritative when the platform cannot report capacity.
        }
    }

    private static void DeletePartialFile(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliExitException(1, $"Could not remove the incomplete Avatar Engine ZIP at '{partialPath}'. Remove it and run the installation again.");
        }
    }

    private static void DeletePartialFileBestEffort(string partialPath)
    {
        try
        {
            DeletePartialFile(partialPath);
        }
        catch (CliExitException exception)
        {
            CliRender.Failure(exception.Message);
        }
    }

    private sealed record ReleaseIdentity(
        string ObjectUri,
        string Version,
        string Platform,
        long SizeBytes,
        byte[] Sha256)
    {
        public string? ETag { get; set; }

        public AvatarReleaseIdentity ToPublicIdentity() => new(Version, Platform, SizeBytes, Convert.ToHexString(Sha256), "published");

        public bool Matches(ReleaseIdentity other) =>
            string.Equals(ObjectUri, other.ObjectUri, StringComparison.Ordinal) &&
            string.Equals(Version, other.Version, StringComparison.Ordinal) &&
            string.Equals(Platform, other.Platform, StringComparison.Ordinal) &&
            SizeBytes == other.SizeBytes &&
            CryptographicOperations.FixedTimeEquals(Sha256, other.Sha256);
    }

    private sealed class RetryableDownloadException(
        string message,
        TimeSpan? delay = null,
        bool isForbidden = false) : Exception(message)
    {
        public TimeSpan Delay { get; } = delay ?? RetryDelay;
        public bool IsForbidden { get; } = isForbidden;
    }
}
