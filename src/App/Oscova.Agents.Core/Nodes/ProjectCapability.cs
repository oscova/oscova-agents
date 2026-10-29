namespace Oscova.Agents.Core.Nodes;

/// <summary>Names the project operations understood by the server and the installed node.</summary>
public static class ProjectCapability
{
    public const string Id = "projects";
    public const int Version = 5;
    public const string ReadEditableFile = "project_read_editable_file";
    public const string SaveEditableFile = "project_save_editable_file";
    public const string ReadFileChunk = "project_read_file_chunk";
    public const int FileChunkBytes = SkillChunkBytes;
    public const string RunSkill = "project_run_skill";
    public const string ReadSkillChunk = "project_read_skill_chunk";
    public const int SkillChunkBytes = 256 * 1024;
    public const string ValidateRoot = "project_validate_root";
    public const string Environment = "project_environment";
    public const string ListFiles = "project_list_files";
    public const string Preview = "project_preview_file";
    public const string Changes = "project_changes";
    public const string Change = "project_change";
}
