namespace Oscova.Agents.Tasks;

/// <summary>Names the files and directories that make up framework task persistence.</summary>
internal static class AgentTaskFileLayout
{
    internal const string TasksDirectory = "tasks";
    internal const string TaskFile = "task.md.enc";
    internal const string RunsDirectory = "runs";
    internal const string RunIndexFile = "index.jsonl";
    internal const string TranscriptExtension = ".jsonl";
    internal const string SessionFileSuffix = ".session.json.enc";
}