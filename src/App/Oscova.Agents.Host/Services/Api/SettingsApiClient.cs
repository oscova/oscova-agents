using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Forms;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Credentials;
using Oscova.Agents.Core.ApiContracts.Files;
using Oscova.Agents.Core.ApiContracts.Prompts;
using Oscova.Agents.Core.ApiContracts.Settings;
using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Core.ApiContracts.Plugins.Registry;
using Oscova.Agents.Core.ApiContracts.Privacy;
using Oscova.Agents.Core.ApiContracts.Skills.Registry;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services.Api;

public sealed class SettingsApiClient(HttpClient httpClient) : ApiClientBase
{
    public async Task<UiSettingsSummary> GetUiSettingsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<UiSettingsSummary>(ApiRoutes.Settings.Ui, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("UI settings response was empty.");
    }

    public async Task SaveUiSettingsAsync(SaveUiSettingsRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Settings.Ui, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteAllConversationsAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Settings.Conversations, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<PrivacySettingsSummary> GetPrivacySettingsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<PrivacySettingsSummary>(ApiRoutes.Settings.Privacy, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Privacy settings response was empty.");
    }

    public async Task SavePrivacySettingsAsync(UpdatePrivacySettingsRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Settings.Privacy, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Flat list of the user's non-managed credentials â€” backs the model/embedding provider dropdowns.</summary>
    public async Task<IReadOnlyList<CredentialListItem>> GetCredentialsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<CredentialListItem>>(ApiRoutes.Credentials.List, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>The projected Credentials page: declared slots merged with saved rows, plus free-form credentials.</summary>
    public async Task<CredentialsContract> GetCredentialsViewAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<CredentialsContract>(ApiRoutes.Credentials.Base, ct)
.ConfigureAwait(false) ?? new CredentialsContract();
    }

    /// <summary>One credential slot for the editor â€” a declared slot or a free-form row.</summary>
    public async Task<CredentialSlotContract?> GetCredentialAsync(string credentialKey, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<CredentialSlotContract>(ApiRoutes.Credentials.ByKey(credentialKey), ct).ConfigureAwait(false);
    }

    /// <summary>Returns validation details or a required interruption confirmation, or null after saving.</summary>
    public async Task<ErrorResult?> SaveCredentialAsync(SaveCredentialRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Credentials.Base, request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) { return null; }

        if (response.StatusCode == HttpStatusCode.Conflict && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (json.TrimStart().StartsWith('{'))
            {
                return System.Text.Json.JsonSerializer.Deserialize<ErrorResult>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            return new ErrorResult { Message = await ReadProblemMessageAsync(response, "The credential could not be saved.", ct).ConfigureAwait(false) };
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return null;
    }

    private static async Task<string> ReadProblemMessageAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        // BadRequest("text")/Conflict("text") under [ApiController] serializes as a JSON-encoded string.
        // ProblemDetails/ValidationProblemDetails come back as objects â€” keep the raw body in that case.
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? fallback;
            }
            catch (System.Text.Json.JsonException)
            {
                return raw;
            }
        }

        try
        {
            var error = System.Text.Json.JsonSerializer.Deserialize<ErrorResult>(raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return error.Message;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return raw;
    }

    public async Task<DeleteCredentialConflict?> DeleteCredentialAsync(string credentialKey, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Credentials.ByKey(credentialKey), ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await response.Content.ReadFromJsonAsync<DeleteCredentialConflict>(cancellationToken: ct)
.ConfigureAwait(false) ?? new DeleteCredentialConflict();
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return null;
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListFilesAsync(string? path, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<FileSystemEntry>>(ApiRoutes.Files.WithRoot(ApiRoutes.Files.List(path), root), ct).ConfigureAwait(false) ?? [];
    }

    public async Task CreateDirectoryAsync(string? path, string name, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Files.WithRoot(ApiRoutes.Files.Directories, root),
            new CreateFileRequest { Path = path, Name = name },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task CreateFileAsync(string? path, string name, string? content = null, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Files.WithRoot(ApiRoutes.Files.Documents, root),
            new CreateFileRequest { Path = path, Name = name, Content = content },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteFileSystemEntryAsync(string path, bool isDirectory, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Files.WithRoot(ApiRoutes.Files.Delete(path, isDirectory), root), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string? path, IBrowserFile file, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(path))
        {
            form.Add(new StringContent(path), "Path");
        }

        var stream = file.OpenReadStream(UserFileLimits.MaxUploadSizeBytes, ct);
        await using (stream.ConfigureAwait(false))
        {
            using var fileContent = new StreamContent(stream);
        form.Add(fileContent, "File", file.Name);

        using var response = await httpClient.PostAsync(ApiRoutes.Files.WithRoot(ApiRoutes.Files.Upload, root), form, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        }
    }

    public Task<Stream> DownloadFileAsync(string path, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        return httpClient.GetStreamAsync(ApiRoutes.Files.WithRoot(ApiRoutes.Files.Download(path), root), ct);
    }

    public async Task<FileTextDocument> ReadFileTextAsync(string path, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Files.WithRoot(ApiRoutes.Files.Text(path), root), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<FileTextDocument>(ct).ConfigureAwait(false) ?? new FileTextDocument();
    }

    public async Task WriteFileTextAsync(string path, string content, bool encrypt, FileRoot root = FileRoot.Home, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.Files.WithRoot(ApiRoutes.Files.TextWrite, root),
            new WriteFileTextRequest { Path = path, Content = content, Encrypt = encrypt },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileBackupSummary>> ListFileBackupsAsync(string path, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<FileBackupSummary>>(ApiRoutes.Files.Backups(path), ct).ConfigureAwait(false) ?? [];
    }

    public async Task<FileBackupTextDocument> ReadFileBackupTextAsync(string path, string backupId, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<FileBackupTextDocument>(ApiRoutes.Files.BackupText(path, backupId), ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Backup response was empty.");
    }

    public async Task RestoreFileBackupAsync(string path, string backupId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Files.RestoreBackup(path, backupId), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Gets safe provider and per-file status for the current user's Library index.</summary>
    public async Task<LibraryIndexView> GetLibraryIndexAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<LibraryIndexView>(ApiRoutes.Files.LibraryIndex, ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Library index response was empty.");
    }

    public async Task<PromptCollection> GetPromptsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<PromptCollection>(ApiRoutes.Prompts.Base, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Prompt response was empty.");
    }

    public async Task SavePromptAsync(string promptKey, SavePromptRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.Prompts.ByKey(promptKey),
            request,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillDefinition>> GetInstalledSkillsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<SkillDefinition>>(ApiRoutes.Skills.Base, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>Publishes an operator skill or removes its managed recipient copies.</summary>
    public async Task<SkillPublicationResult?> PublishSkillAsync(string skillName, bool enabled, bool confirmed, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsync(
            $"{ApiRoutes.Skills.Base}/{Uri.EscapeDataString(skillName)}/shared?enabled={enabled}&confirmed={confirmed}",
            null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<SkillPublicationResult>(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The server returned no publication result.");
    }
    public async Task DeleteSkillAsync(string skillName, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Skills.Delete(skillName), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<ImportedSkillResult> ImportSkillAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName,
            FileNameStar = fileName
        };
        form.Add(fileContent, "File", fileName);

        using var response = await httpClient.PostAsync(ApiRoutes.Skills.Import, form, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var error = await ReadProblemMessageAsync(response, "The skill file could not be imported.", ct).ConfigureAwait(false);
            throw new SkillImportValidationException(error);
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<ImportedSkillResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Skill import response was empty.");
    }

    public async Task<SkillRegistryCatalogResponse> GetSkillRegistryCatalogAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.SkillRegistry.Catalog, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<SkillRegistryCatalogResponse>(cancellationToken: ct)
            .ConfigureAwait(false) ?? new SkillRegistryCatalogResponse();
    }

    public async Task<string?> GetSkillRegistryDescriptionAsync(SkillRegistrySource source, string skillPath, CancellationToken ct = default)
    {
        var url = ApiRoutes.SkillRegistry.Description(source.Owner, source.Repo, source.Branch, skillPath);
        return await httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>Installs a remote skill; returns null on success, an inline error message on conflict or upstream failure.</summary>
    public async Task<(ImportedSkillResult? Result, string? Error)> InstallRemoteSkillAsync(InstallRemoteSkillRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.SkillRegistry.Install, request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<ImportedSkillResult>(cancellationToken: ct).ConfigureAwait(false);
            return (result, null);
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadGateway or HttpStatusCode.BadRequest)
        {
            var error = await ReadProblemMessageAsync(response, "The skill could not be installed.", ct).ConfigureAwait(false);
            return (null, error);
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return (null, null);
    }

    public async Task<SkillRegistrySettings.Snapshot> GetSkillRegistrySettingsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<SkillRegistrySettings.Snapshot>(ApiRoutes.SkillRegistry.Settings, ct)
.ConfigureAwait(false) ?? new SkillRegistrySettings.Snapshot();
    }

    public async Task SaveSkillRegistrySettingsAsync(UpdateSkillRegistrySettingsRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.SkillRegistry.Settings, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<PluginRegistryCatalogResponse> SearchPluginRegistryAsync(string query, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<PluginRegistryCatalogResponse>(ApiRoutes.PluginRegistry.Search(query), ct)
.ConfigureAwait(false) ?? new PluginRegistryCatalogResponse();
    }

    public async Task<RemotePluginCatalogEntry?> GetPluginRegistryMetadataAsync(string packageId, CancellationToken ct = default)
    {
        var response = await httpClient.GetFromJsonAsync<PluginRegistryMetadataResponse>(ApiRoutes.PluginRegistry.Metadata(packageId), ct).ConfigureAwait(false);
        return response?.Entry;
    }

    /// <summary>Installs a remote plugin and returns a typed failure when replacement needs confirmation.</summary>
    public async Task<PluginInstallFailure?> InstallRemotePluginAsync(InstallRemotePluginRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.PluginRegistry.Install, request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) { return null; }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadFromJsonAsync<PluginInstallFailure>(cancellationToken: ct).ConfigureAwait(false);
            return body ?? new PluginInstallFailure("The plugin could not be installed.");
        }

        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return new PluginInstallFailure(await ReadProblemMessageAsync(response,
                "The plugin could not be installed.", ct).ConfigureAwait(false));
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> GetGitHubSkillsCredentialConfiguredAsync(CancellationToken ct = default)
    {
        var status = await httpClient.GetFromJsonAsync<GitHubSkillsCredentialStatusContract>(ApiRoutes.SkillRegistry.AdminCredential, ct).ConfigureAwait(false);
        return status?.IsConfigured ?? false;
    }

    public async Task SaveGitHubSkillsCredentialAsync(string? token, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.SkillRegistry.AdminCredential,
            new SaveGitHubSkillsCredentialContract { Token = token },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task ClearGitHubSkillsCredentialAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.SkillRegistry.AdminCredential, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }
}
