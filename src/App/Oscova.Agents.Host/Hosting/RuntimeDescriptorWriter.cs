using System.Diagnostics;
using System.Text.Json;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Host.Hosting;

/// <summary>
/// Writes ~/.oscova/runtime.json on startup so external tooling (and a future `oscova attach`
/// verb) can discover a running instance. The file is deleted on graceful shutdown.
/// </summary>
public sealed class RuntimeDescriptorWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    private RuntimeDescriptorWriter(string path) => _path = path;

    public static string RuntimePath => Path.Combine(ResolveUserProfileDirectory(), ".oscova", "runtime.json");

    public static RuntimeDescriptorWriter WriteOnStart(WebApplication app, string controlToken)
    {
        var dir = Path.GetDirectoryName(RuntimePath) ?? throw new InvalidOperationException("Runtime path directory is unavailable.");
        CliFileSecurity.EnsurePrivateDirectory(dir);

        using var process = Process.GetCurrentProcess();
        var payload = new RuntimeDescriptor
        {
            Pid = Environment.ProcessId,
            Urls = app.Urls.ToArray(),
            StartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()),
            ProcessStartIdentifier = ProcessIdentity.GetStartIdentifier(process),
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            AppDataPath = Path.GetFullPath(PathUtil.AppDataPath),
            ControlToken = controlToken,
            RestartArguments = app.Services.GetService<ApplicationUpdateHostOptions>()?.ToolInstallation?.RestartArguments.ToArray() ?? []
        };
        File.WriteAllText(RuntimePath, JsonSerializer.Serialize(payload, JsonOptions));
        return new RuntimeDescriptorWriter(RuntimePath);
    }

    public static RuntimeDescriptor? ReadOrNull()
    {
        try
        {
            if (!File.Exists(RuntimePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<RuntimeDescriptor>(File.ReadAllText(RuntimePath), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<RuntimeDescriptor?> TryReadLiveAsync(HttpClient httpClient, CancellationToken ct)
    {
        var descriptor = ReadOrNull();
        if (descriptor is null)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(descriptor.Pid);
            if (descriptor.StartedAtUtc != default)
            {
                var processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                if (processStartedAtUtc > descriptor.StartedAtUtc.AddSeconds(5))
                {
                    DeleteStale();
                    return null;
                }
            }
        }
        catch (ArgumentException)
        {
            DeleteStale();
            return null;
        }
        catch (Exception)
        {
            // Process metadata can be unavailable even when the same-user HTTP listener is healthy.
            // Keep the descriptor until the liveness probe below has the final say.
        }

        var probeUrls = CliHostUrlSelector.SelectLocalProbeUrls(descriptor.Urls);
        if (probeUrls.Count == 0)
        {
            DeleteStale();
            return null;
        }

        foreach (var probeUrl in probeUrls)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var requestUri = new Uri(new Uri(probeUrl.TrimEnd('/') + "/"), "api/host/version");
                using var response = await httpClient.GetAsync(requestUri, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return descriptor;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    public static void DeleteStale()
    {
        try
        {
            if (File.Exists(RuntimePath))
            {
                File.Delete(RuntimePath);
            }
        }
        catch (Exception)
        {
        }
    }

    public void Delete()
    {
        try
        {
            var descriptor = ReadOrNull();
            if (descriptor?.Pid == Environment.ProcessId && File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception)
        {
            // Best effort — a stale descriptor is acceptable; a crash would also leave one behind.
        }
    }

    private static string ResolveUserProfileDirectory()
    {
        var primary = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("USERPROFILE")
            : Environment.GetEnvironmentVariable("HOME");

        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        var fallback = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("HOME")
            : Environment.GetEnvironmentVariable("USERPROFILE");

        return string.IsNullOrWhiteSpace(fallback)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : fallback;
    }
}

public sealed class RuntimeDescriptor
{
    public int Pid { get; set; }
    public string[] Urls { get; set; } = [];
    public DateTimeOffset StartedAtUtc { get; set; }
    public long ProcessStartIdentifier { get; set; }
    public string ControlToken { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string AppDataPath { get; set; } = string.Empty;

    /// <summary>The arguments that restart this Host as it was started, used after an update replaces it.</summary>
    public string[] RestartArguments { get; set; } = [];
}
