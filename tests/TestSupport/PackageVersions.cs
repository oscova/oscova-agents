using System.Reflection;

namespace Oscova.Agents.Tests.TestSupport;

/// <summary>Reads first-party package versions embedded from Versions.props so tests never hardcode them.</summary>
internal static class PackageVersions
{
    public static string Get(string packageId) =>
        typeof(PackageVersions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == packageId).Value!;
}
