namespace Oscova.Agents.Plugins.Docker;

/// <summary>Identifies the Docker prerequisite that prevents sandbox execution.</summary>
public enum DockerReadinessFailure { NotFound, StartFailed, Unreachable, TimedOut }

/// <summary>Provides a Docker failure reason for runtime errors and localized settings feedback.</summary>
public sealed class DockerReadinessException : InvalidOperationException
{
    /// <summary>Creates an actionable Docker readiness failure with its underlying cause.</summary>
    public DockerReadinessException(DockerReadinessFailure reason, Exception? innerException = null)
        : base(reason switch
        {
            DockerReadinessFailure.NotFound => "Docker was not found on the machine running Oscova. Install Docker Desktop or Docker Engine and ensure docker is on PATH, then retry.",
            DockerReadinessFailure.StartFailed => "Oscova could not start Docker. Check the Docker installation and the operating-system account's permissions, then retry.",
            DockerReadinessFailure.Unreachable => "Oscova could not connect to Docker. Start Docker Desktop or Docker Engine, or check the Docker connection and account permissions, then retry.",
            DockerReadinessFailure.TimedOut => "Docker did not respond within 10 seconds. Wait for Docker to finish starting, check its connection, then retry.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        }, innerException)
    {
        Reason = reason;
    }

    public DockerReadinessFailure Reason { get; }
}
