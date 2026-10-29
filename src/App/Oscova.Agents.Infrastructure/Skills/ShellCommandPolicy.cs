using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.ApiContracts.Chat;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Provides the shared finite deny list for shell execution.</summary>
internal static class ShellCommandPolicy
{
    private const string Device = "/dev/(?:sd[a-z][0-9]*|nvme[0-9]+n[0-9]+(?:p[0-9]+)?|disk[0-9]+(?:s[0-9]+)?)";
    private const string PosixProtectedTarget = """(?:/|/\*|~(?:/|/\*)?|\$HOME(?:/|/\*)?|['"]/(?:\*)?['"]|"\$HOME(?:/|/\*)?")""";
    private const string PowerShellProtectedTarget = """(?:[a-z]:\\(?:\*)?|['"][a-z]:\\(?:\*)?['"]|\$(?:HOME|env:USERPROFILE)|"\$(?:HOME|env:USERPROFILE)")""";

    // Literal command shapes catch selected accidents only.
    // Broader assessment needs a separately designed parser and approval contract.
    private static readonly string[] DestructiveShapes =
    [
        $"^(?:sudo +)?rm +(?:-rf|-fr|-r +-f|-f +-r|-r) +{PosixProtectedTarget}$",
        $"^Remove-Item +(?:(?:-Recurse(?: +-Force)? +{PowerShellProtectedTarget})|(?:{PowerShellProtectedTarget} +-Recurse(?: +-Force)?))$",
        $@"^(?:sudo +)?mkfs(?:\.(?:ext2|ext3|ext4|xfs|btrfs))? +{Device}$",
        $"^(?:sudo +)?dd +if=/dev/(?:zero|urandom) +of={Device}$",
        @"^:\s*\(\s*\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:$"
    ];

    public static ShellPolicy Instance { get; } = new(DestructiveShapes);
    private static readonly ShellPolicy FullAccess = new();

    public static ShellPolicy For(RunContext run) =>
        run.PermissionPreset == ChatPermissionPreset.FullAccess ? FullAccess : Instance;
}
