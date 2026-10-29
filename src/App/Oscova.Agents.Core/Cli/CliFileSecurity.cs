using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Oscova.Agents.Core.Cli;

/// <summary>Creates directories restricted to the current OS user.</summary>
public static class CliFileSecurity
{
    public static void EnsurePrivateDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);

        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsDirectory(directoryPath);
            return;
        }

        File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsDirectory(string directoryPath)
    {
        var currentUser = GetCurrentWindowsUser();
        var directoryInfo = new DirectoryInfo(directoryPath);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directoryInfo.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier GetCurrentWindowsUser()
    {
        return WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows user identity is unavailable.");
    }
}
