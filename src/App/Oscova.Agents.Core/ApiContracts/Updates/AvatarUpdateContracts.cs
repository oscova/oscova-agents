namespace Oscova.Agents.Core.ApiContracts.Updates;

/// <summary>Records verified Avatar content without retaining a signed download URL.</summary>
public sealed record AvatarReleaseIdentity(string? Version, string Platform, long SizeBytes, string Sha256, string Source)
{
    /// <summary>Validates the publication and locally persisted receipt using the same limits.</summary>
    public bool IsValid() => Platform == "win-x64" && SizeBytes > 0 && Sha256 is { Length: 64 } && Sha256.All(char.IsAsciiHexDigit) &&
        (Source == "local-zip" && Version is null || Source == "published" && Version is { Length: >= 1 and <= 64 } &&
            Version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'));

    /// <summary>Compares content independently of the publisher's release label.</summary>
    public bool MatchesContent(AvatarReleaseIdentity other) => Platform == other.Platform && SizeBytes == other.SizeBytes &&
        string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reports retained download bytes and verification or restart activity.</summary>
public sealed record AvatarDownloadProgress(long ReceivedBytes, long TotalBytes, TimeSpan? RetryIn = null, string? Message = null);

/// <summary>Identifies the local process owned by Avatar lifecycle operations.</summary>
public sealed record AvatarEngineRuntime(int Pid, DateTimeOffset StartedAtUtc, string Address, int Port);

/// <summary>Separates local installation and tracked process state from bridge connectivity.</summary>
public sealed record AvatarEngineStatus(bool Supported, bool Installed, bool Running, int? Pid, string? Address, int? Port, AvatarReleaseIdentity? Release = null);

/// <summary>Compares local and published content before replacement consent.</summary>
public sealed record AvatarInstallationCheck(string State, AvatarReleaseIdentity? Installed, AvatarReleaseIdentity? Published);

/// <summary>Supplies the current dashboard action and the Host-owned operation result.</summary>
public sealed record AvatarUpdateStatus(bool Managed, bool Supported, bool Installed, bool Running,
    AvatarReleaseIdentity? InstalledRelease, AvatarReleaseIdentity? AvailableRelease, string State,
    string ComputerName, string? Error = null, long ReceivedBytes = 0, long TotalBytes = 0)
{
    public bool IsBusy => State is "Downloading" or "Verifying" or "Installing" or "Restarting" or "Stopping";
    public bool CanInstall => Managed && Supported && !IsBusy && AvailableRelease is not null &&
        (!Installed || InstalledRelease?.MatchesContent(AvailableRelease) != true);
}

/// <summary>Confirms reviewed content and any local process interruption.</summary>
public sealed record StartAvatarUpdateRequest(AvatarReleaseIdentity Release, bool ConfirmInstallAndStop);
