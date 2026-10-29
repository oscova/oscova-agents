using Oscova.Agents.Hooks;
using Oscova.Agents.Persistence;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Validates hook settings before they are persisted or used by the dispatcher.
/// </summary>
public static class HookSettingsValidator
{
    public const string TasksRunScope = "tasks:run";

    public static IReadOnlyList<HookDefinition> NormalizeOrThrow(IEnumerable<HookDefinition> hooks)
    {
        var normalized = new List<HookDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hook in hooks)
        {
            var copy = NormalizeHook(hook);
            if (!ids.Add(copy.Id))
            {
                throw new InvalidOperationException($"Duplicate hook id '{copy.Id}'.");
            }

            ValidateHook(copy);
            normalized.Add(copy);
        }

        return normalized;
    }

    public static IReadOnlyList<HookApiKeyDefinition> NormalizeApiKeysOrThrow(IEnumerable<HookApiKeyDefinition> apiKeys)
    {
        var normalized = new List<HookApiKeyDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var apiKey in apiKeys)
        {
            var copy = NormalizeApiKey(apiKey);
            if (!ids.Add(copy.Id))
            {
                throw new InvalidOperationException($"Duplicate hook API key id '{copy.Id}'.");
            }

            if (!prefixes.Add(copy.TokenPrefix))
            {
                throw new InvalidOperationException($"Duplicate hook API key token prefix '{copy.TokenPrefix}'.");
            }

            ValidateApiKey(copy);
            normalized.Add(copy);
        }

        return normalized;
    }

    private static HookApiKeyDefinition NormalizeApiKey(HookApiKeyDefinition apiKey)
    {
        var scopes = (apiKey.Scopes ?? [])
            .Select(scope => scope?.Trim() ?? string.Empty)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HookApiKeyDefinition
        {
            Id = apiKey.Id?.Trim() ?? string.Empty,
            Name = apiKey.Name?.Trim() ?? string.Empty,
            IsEnabled = apiKey.IsEnabled,
            TokenPrefix = apiKey.TokenPrefix?.Trim() ?? string.Empty,
            TokenHash = apiKey.TokenHash?.Trim() ?? string.Empty,
            Scopes = scopes.Count == 0 ? [TasksRunScope] : scopes,
            AllowedTaskSlugs = (apiKey.AllowedTaskSlugs ?? [])
                .Where(slug => !string.IsNullOrWhiteSpace(slug))
                .Select(slug => Slugs.ForTaskTrigger(slug))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CreatedAt = apiKey.CreatedAt == default ? DateTimeOffset.UtcNow : apiKey.CreatedAt,
            LastUsedAt = apiKey.LastUsedAt
        };
    }

    private static void ValidateApiKey(HookApiKeyDefinition apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey.Id))
        {
            throw new InvalidOperationException("Hook API key id is required.");
        }

        if (string.IsNullOrWhiteSpace(apiKey.TokenPrefix))
        {
            throw new InvalidOperationException($"Hook API key '{apiKey.Id}' token prefix is required.");
        }

        if (string.IsNullOrWhiteSpace(apiKey.TokenHash))
        {
            throw new InvalidOperationException($"Hook API key '{apiKey.Id}' token hash is required.");
        }

        foreach (var scope in apiKey.Scopes)
        {
            if (!string.Equals(scope, TasksRunScope, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Hook API key '{apiKey.Id}' contains unsupported scope '{scope}'.");
            }
        }
    }

    private static HookDefinition NormalizeHook(HookDefinition hook)
    {
        return new HookDefinition
        {
            Id = string.IsNullOrWhiteSpace(hook.Id) ? Guid.NewGuid().ToString("N") : hook.Id.Trim(),
            Name = hook.Name?.Trim() ?? string.Empty,
            IsEnabled = hook.IsEnabled,
            Events = (hook.Events ?? []).Distinct().ToList(),
            HandlerType = hook.HandlerType,
            FailureMode = hook.FailureMode,
            TimeoutSeconds = hook.TimeoutSeconds is >= 1 and <= 300 ? hook.TimeoutSeconds : 10,
            Command = NormalizeCommand(hook.Command),
            Http = NormalizeHttp(hook.Http)
        };
    }

    private static void ValidateHook(HookDefinition hook)
    {
        if (hook.Events.Count == 0)
        {
            throw new InvalidOperationException($"Hook '{hook.Id}' must contain at least one event.");
        }

        foreach (var hookEvent in hook.Events)
        {
            if (!Enum.IsDefined(hookEvent))
            {
                throw new InvalidOperationException($"Hook '{hook.Id}' contains an unsupported event.");
            }
        }

        if (!Enum.IsDefined(hook.FailureMode))
        {
            throw new InvalidOperationException($"Hook '{hook.Id}' has an unsupported failure mode.");
        }

        switch (hook.HandlerType)
        {
            case HookHandlerType.Command:
                ValidateCommandHook(hook);
                break;
            case HookHandlerType.Http:
                ValidateHttpHook(hook);
                break;
            default:
                throw new InvalidOperationException($"Hook '{hook.Id}' has an unsupported handler type.");
        }
    }

    private static void ValidateCommandHook(HookDefinition hook)
    {
        if (hook.Command is null || string.IsNullOrWhiteSpace(hook.Command.FileName))
        {
            throw new InvalidOperationException($"Command hook '{hook.Id}' must specify a file name.");
        }

        if (hook.Http is not null)
        {
            throw new InvalidOperationException($"Command hook '{hook.Id}' cannot include HTTP options.");
        }
    }

    private static void ValidateHttpHook(HookDefinition hook)
    {
        if (hook.Http is null ||
            !Uri.TryCreate(hook.Http.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"HTTP hook '{hook.Id}' must specify an HTTP or HTTPS URL.");
        }

        if (hook.Command is not null)
        {
            throw new InvalidOperationException($"HTTP hook '{hook.Id}' cannot include command options.");
        }

        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in hook.Http.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                throw new InvalidOperationException($"HTTP hook '{hook.Id}' contains a static header without a name.");
            }

            if (!headerNames.Add(header.Name))
            {
                throw new InvalidOperationException($"HTTP hook '{hook.Id}' contains duplicate header '{header.Name}'.");
            }
        }

        foreach (var header in hook.Http.SecretHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                throw new InvalidOperationException($"HTTP hook '{hook.Id}' contains a secret header without a name.");
            }

            if (string.IsNullOrWhiteSpace(header.CredentialKey))
            {
                throw new InvalidOperationException($"HTTP hook '{hook.Id}' secret header '{header.Name}' must specify a credential key.");
            }

            if (!headerNames.Add(header.Name))
            {
                throw new InvalidOperationException($"HTTP hook '{hook.Id}' contains duplicate header '{header.Name}'.");
            }
        }
    }

    private static CommandHookOptions? NormalizeCommand(CommandHookOptions? command)
    {
        if (command is null)
        {
            return null;
        }

        return new CommandHookOptions
        {
            FileName = command.FileName?.Trim() ?? string.Empty,
            Arguments = (command.Arguments ?? []).Select(argument => argument?.Trim() ?? string.Empty).ToList(),
            WorkingDirectory = NormalizeNullable(command.WorkingDirectory)
        };
    }

    private static HttpHookOptions? NormalizeHttp(HttpHookOptions? http)
    {
        if (http is null)
        {
            return null;
        }

        return new HttpHookOptions
        {
            Url = http.Url?.Trim() ?? string.Empty,
            Headers = (http.Headers ?? []).Select(header => new HookHeader
            {
                Name = header.Name?.Trim() ?? string.Empty,
                Value = header.Value ?? string.Empty
            }).ToList(),
            SecretHeaders = (http.SecretHeaders ?? []).Select(header => new HookSecretHeader
            {
                Name = header.Name?.Trim() ?? string.Empty,
                CredentialKey = header.CredentialKey?.Trim() ?? string.Empty,
                FieldName = NormalizeNullable(header.FieldName)
            }).ToList()
        };
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
