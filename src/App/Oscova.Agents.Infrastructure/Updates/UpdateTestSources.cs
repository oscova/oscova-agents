using System.Net;

namespace Oscova.Agents.Infrastructure.Updates;

/// <summary>Confines controlled update sources to explicitly admitted test processes.</summary>
public static class UpdateTestSources
{
    public const string ModeVariable = "OSCOVA_UPDATE_TEST_MODE";
    public const string PackageVariable = "OSCOVA_UPDATE_TEST_SOURCE";
    public const string AvatarVariable = "OSCOVA_AVATAR_TEST_SOURCE";

    /// <summary>Rejects incomplete or forbidden test configuration before App startup has side effects.</summary>
    public static void ValidateAppEnvironment()
    {
        if (Environment.GetEnvironmentVariable(ModeVariable) == "1")
        {
            EnsureAdmitted();
            if (GetPackageDirectory() is null || GetAvatarSource() is null)
            {
                throw new InvalidOperationException("Update test mode requires both controlled package and Avatar sources.");
            }
        }
        else
        {
            _ = GetPackageDirectory();
            _ = GetAvatarSource();
        }
    }

    /// <summary>Returns an admitted local package directory, or the ordinary production selection.</summary>
    public static string? GetPackageDirectory()
    {
        var path = Environment.GetEnvironmentVariable(PackageVariable);
        if (string.IsNullOrWhiteSpace(path)) { return null; }

        EnsureAdmitted();
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new InvalidOperationException("The application update test source must be an existing absolute directory.");
        }

        return Path.GetFullPath(path);
    }

    /// <summary>Returns an admitted loopback publication URL without enabling arbitrary download hosts.</summary>
    public static Uri? GetAvatarSource()
    {
        var source = Environment.GetEnvironmentVariable(AvatarVariable);
        if (string.IsNullOrWhiteSpace(source)) { return null; }

        EnsureAdmitted();
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Port <= 0 ||
            uri.AbsolutePath != "/api/downloads/avatar-engine" ||
            !source[..source.IndexOf('/', "http://".Length)].EndsWith(":" + uri.Port, StringComparison.Ordinal) ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException("The Avatar test source must be a loopback HTTP publication URL without credentials, query or fragment.");
        }

        return uri;
    }

    private static void EnsureAdmitted()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var allowed = environment == "IntegrationTests";
#if DEBUG
        allowed |= environment == "Development";
#endif
        if (!allowed || Environment.GetEnvironmentVariable(ModeVariable) != "1")
        {
            throw new InvalidOperationException("Update test sources are not permitted in this process.");
        }
    }
}
