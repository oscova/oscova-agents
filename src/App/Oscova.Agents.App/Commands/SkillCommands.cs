using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Host.Constants;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class SkillImportSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("Skill package to import, as a zip file.")]
    public string File { get; init; } = string.Empty;
}

/// <summary>
/// Imports a skill package into the signed-in user's skill library.
/// </summary>
public sealed class SkillsImportCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<SkillImportSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, SkillImportSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            throw new CliExitException(4, $"No file at '{settings.File}'.");
        }

        await using var stream = File.OpenRead(settings.File);
        using var content = new MultipartFormDataContent { { new StreamContent(stream), "file", Path.GetFileName(settings.File) } };

        using var response = await CliRender.StatusAsync(
            $"Importing {Path.GetFileName(settings.File)}...", settings,
            () => client.PostAsync(ApiRoutes.Skills.Import, content, cancellationToken)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new CliExitException(4, "A skill with that name is already installed. Remove it first with 'oscova skills remove <SKILL_NAME>'.");
        }

        await CliHttpErrors.EnsureSuccessAsync(response, "Importing skill", cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ImportedSkillResult>(CliJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new CliExitException(1, "The host returned an empty import result.");

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(result, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var message = $"Imported {result.Name}.";
        if (!settings.Quiet && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Skill imported", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        foreach (var warning in result.Warnings)
        {
            CliRender.Warning(warning);
        }

        return 0;
    }
}

public sealed class SkillRemoveSettings : OscovaCommandSettings
{
    [CommandArgument(0, "<SKILL_NAME>")]
    [Description("Skill to remove. List installed skills with 'oscova skills list'.")]
    public string SkillName { get; init; } = string.Empty;

    [CommandOption("--yes")]
    [Description("Remove without asking for confirmation.")]
    public bool Yes { get; init; }
}

/// <summary>
/// Removes an installed skill and drops it from every agent that allowed it.
/// </summary>
public sealed class SkillsRemoveCommand(CliHostResolver resolver, HostApiClient apiClient) : HostCommandBase<SkillRemoveSettings>(resolver, apiClient)
{
    protected override async Task<int> RunAsync(HttpClient client, SkillRemoveSettings settings, CancellationToken cancellationToken)
    {
        CliRender.ConfirmDestructive(settings, settings.Yes, $"Skill '{settings.SkillName}' will be uninstalled and removed from every agent that uses it.");

        using var response = await CliRender.StatusAsync(
            $"Removing {settings.SkillName}...", settings,
            () => client.DeleteAsync(ApiRoutes.Skills.Delete(settings.SkillName), cancellationToken)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliExitException(4, $"No skill '{settings.SkillName}'. List skills with 'oscova skills list'.");
        }

        await CliHttpErrors.EnsureSuccessAsync(response, "Removing skill", cancellationToken).ConfigureAwait(false);
        var message = $"Removed {settings.SkillName}.";
        if (!settings.Json && !settings.Quiet &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
        {
            AnsiConsole.Write(CliRender.Card("Skill removed", new Text(message, CliTheme.Value), CliTheme.Success));
        }
        else
        {
            CliRender.Success(message);
        }

        return 0;
    }
}
