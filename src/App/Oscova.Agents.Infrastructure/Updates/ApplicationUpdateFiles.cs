using System.Diagnostics;
using System.Text.Json;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Updates;

/// <summary>Owns the protected application update attempt records under the updates directory.</summary>
public static class ApplicationUpdateFiles
{
    public const string UpdateRootEnvironmentVariable = "OSCOVA_UPDATE_ROOT";
    public const string BackupFileName = "environment.oscb";
    public const string RecoveryKeyFileName = "recovery.key";
    public const string RecoveryInstructionsFileName = "recovery.txt";
    public const string LogFileName = "update.log";
    public const string ConsoleLogFileName = "update-console.log";

    private const int MaxDocumentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Gets the canonical private directory that contains application-update operations.</summary>
    public static string UpdatesDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(UpdateRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            var parent = Directory.GetParent(Path.GetFullPath(PathUtil.AppDataPath))?.FullName
                ?? throw new InvalidOperationException("The application update directory cannot be resolved.");
            return Path.Combine(parent, "updates");
        }
    }

    /// <summary>Creates and exclusively locks the shared update lease.</summary>
    public static FileStream AcquireUpdateLease()
    {
        CliFileSecurity.EnsurePrivateDirectory(UpdatesDirectory);
        try
        {
            return new FileStream(
                Path.Combine(UpdatesDirectory, "update.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Another application update is already running.", ex);
        }
    }

    /// <summary>Returns the log that receives update console output when no terminal window can open.</summary>
    public static string PrepareConsoleLog()
    {
        CliFileSecurity.EnsurePrivateDirectory(UpdatesDirectory);
        return Path.Combine(UpdatesDirectory, ConsoleLogFileName);
    }

    /// <summary>Creates the protected directory for a server-generated operation identifier.</summary>
    private static string CreateOperationDirectory(string operationId)
    {
        var path = GetOperationDirectory(operationId);
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("The application update operation already exists.");
        }

        CliFileSecurity.EnsurePrivateDirectory(path);
        return path;
    }

    /// <summary>Returns the protected operation directory for a validated identifier.</summary>
    public static string GetOperationDirectory(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out var parsed) ||
            !string.Equals(parsed.ToString("N"), operationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The application update operation identifier is invalid.");
        }

        var root = Path.GetFullPath(UpdatesDirectory);
        var path = Path.GetFullPath(Path.Combine(root, operationId));
        if (!PathHelper.IsPathWithinDirectory(path, root))
        {
            throw new InvalidOperationException("The application update operation path is invalid.");
        }

        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The application update operation directory cannot be a link.");
        }

        return path;
    }

    /// <summary>Reads and validates one application-update journal.</summary>
    public static ApplicationUpdateOperation ReadOperation(string operationId)
    {
        var operation = Read<ApplicationUpdateOperation>(
            Path.Combine(GetOperationDirectory(operationId), ApplicationUpdateOperation.FileName));
        if (!string.Equals(operation.OperationId, operationId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(operation.SourceVersion) ||
            string.IsNullOrWhiteSpace(operation.TargetVersion) ||
            operation.DataFormatVersion <= 0 ||
            operation.CreatedAtUtc == default ||
            operation.UpdatedAtUtc == default ||
            !Enum.IsDefined(operation.State) ||
            operation.FailedPhase is { } failedPhase && !Enum.IsDefined(failedPhase))
        {
            throw new InvalidOperationException("The application update journal is invalid.");
        }

        ValidateCanonicalPath(operation.AppDataPath, PathUtil.AppDataPath, "environment");
        ValidateCanonicalPath(operation.UpdatesDirectory, UpdatesDirectory, "update");
        return operation;
    }

    /// <summary>Atomically writes one application-update journal.</summary>
    public static void WriteOperation(ApplicationUpdateOperation operation)
    {
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        Write(Path.Combine(GetOperationDirectory(operation.OperationId), ApplicationUpdateOperation.FileName), operation);
    }

    /// <summary>Creates the protected directory and the Started record for a new attempt.</summary>
    public static ApplicationUpdateOperation StartOperation(string sourceVersion, string targetVersion, int dataFormatVersion)
    {
        var operation = new ApplicationUpdateOperation
        {
            OperationId = Guid.NewGuid().ToString("N"),
            SourceVersion = sourceVersion,
            TargetVersion = targetVersion,
            DataFormatVersion = dataFormatVersion,
            AppDataPath = Path.GetFullPath(PathUtil.AppDataPath),
            UpdatesDirectory = UpdatesDirectory,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            State = ApplicationUpdateState.Started
        };
        CreateOperationDirectory(operation.OperationId);
        WriteOperation(operation);
        return operation;
    }

    /// <summary>Records the next state of an attempt.</summary>
    public static void Advance(ApplicationUpdateOperation operation, ApplicationUpdateState state)
    {
        operation.State = state;
        WriteOperation(operation);
        AppendLog(operation.OperationId, state.ToString());
    }

    /// <summary>Records a failure with the state the attempt had reached.</summary>
    public static void MarkFailed(ApplicationUpdateOperation operation, string diagnostic)
    {
        if (operation.State != ApplicationUpdateState.Failed)
        {
            operation.FailedPhase = operation.State;
        }

        operation.State = ApplicationUpdateState.Failed;
        operation.Diagnostic = diagnostic[..Math.Min(diagnostic.Length, 2000)];
        WriteOperation(operation);
        AppendLog(operation.OperationId, "Failed: " + diagnostic);
    }

    /// <summary>Lists the readable attempts for this environment, newest first.</summary>
    public static IReadOnlyList<ApplicationUpdateOperation> ListOperations()
    {
        if (!Directory.Exists(UpdatesDirectory))
        {
            return [];
        }

        var operations = new List<ApplicationUpdateOperation>();
        foreach (var directory in Directory.EnumerateDirectories(UpdatesDirectory))
        {
            try
            {
                operations.Add(ReadOperation(Path.GetFileName(directory)));
            }
            catch (InvalidOperationException)
            {
                // Folders from another environment or unreadable records are history, not a current attempt.
            }
        }

        operations.Sort((left, right) => right.CreatedAtUtc.CompareTo(left.CreatedAtUtc));
        return operations;
    }

    /// <summary>Returns the newest attempt for this environment. Only the newest attempt drives startup and recovery.</summary>
    public static ApplicationUpdateOperation? ResolveOperation() => ListOperations().FirstOrDefault();

    /// <summary>Returns the newest attempt when it is unfinished and started from the supplied version.</summary>
    public static ApplicationUpdateOperation? FindUnfinishedFrom(string sourceVersion) =>
        ResolveOperation() is { IsUnfinished: true } operation && VersionsEqual(operation.SourceVersion, sourceVersion)
            ? operation
            : null;

    /// <summary>Removes an attempt folder.</summary>
    public static void DeleteOperation(string operationId) =>
        Directory.Delete(GetOperationDirectory(operationId), recursive: true);

    /// <summary>Deletes completed attempts other than the supplied one and keeps every failed attempt.</summary>
    public static void ApplyRetention(string latestCompletedOperationId)
    {
        foreach (var operation in ListOperations())
        {
            if (operation.State == ApplicationUpdateState.Completed &&
                !string.Equals(operation.OperationId, latestCompletedOperationId, StringComparison.Ordinal))
            {
                DeleteOperation(operation.OperationId);
            }
        }
    }

    /// <summary>Compares package versions the way the update records store them.</summary>
    public static bool VersionsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends a bounded diagnostic line to the private operation log.</summary>
    public static void AppendLog(string operationId, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message.ReplaceLineEndings(" ")}";
        var path = Path.Combine(GetOperationDirectory(operationId), LogFileName);
        File.AppendAllText(path, line[..Math.Min(line.Length, 4096)] + Environment.NewLine);
        ProtectFile(path);
    }

    private static T Read<T>(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxDocumentBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Application update evidence is missing or invalid.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(RetryContention(() => File.ReadAllBytes(path)), JsonOptions)
                ?? throw new InvalidOperationException("Application update evidence is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Application update evidence is invalid.", ex);
        }
    }

    private static void Write<T>(string path, T value, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            RetryContention(() => File.Move(temporaryPath, path, overwrite));
            ProtectFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    // The Host and the update command share these files. Windows refuses a read during a replace with a
    // sharing violation, and refuses a replace while another process reads the file with access denied.
    private static void RetryContention(Action action) => RetryContention(() =>
    {
        action();
        return true;
    });

    private static TResult RetryContention<TResult>(Func<TResult> action)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (elapsed.Elapsed < TimeSpan.FromSeconds(2) &&
                (ex is IOException io && FileContention.IsContention(io) ||
                 ex is UnauthorizedAccessException && OperatingSystem.IsWindows()))
            {
                Thread.Sleep(25);
            }
        }
    }

    private static void ValidateCanonicalPath(string actual, string expected, string label)
    {
        if (string.IsNullOrWhiteSpace(actual) || !PathsEqual(actual, expected))
        {
            throw new InvalidOperationException($"The application update {label} path is invalid.");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    /// <summary>Restricts a private update file to the current user on Unix.</summary>
    public static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
