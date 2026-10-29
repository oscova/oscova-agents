namespace Oscova.Agents.Host.Server.Security;

public static class AgentsServerAuthenticationDefaults
{
    public const string BlazorBearerScheme = "AssistantBlazorBearer";
    public const string HookApiKeyScheme = "OscovaHookApiKey";
    public const string LocalControlScheme = "OscovaLocalControl";
    public const string SmartScheme = "AssistantSmartAuth";

    public const string HostControlPolicy = "HostControl";
    public const string HostStatusPolicy = "HostStatus";
    public const string ApplicationUpdatePolicy = "ApplicationUpdate";
    public const string BrowserBackupExportPolicy = "BrowserBackupExport";
}
