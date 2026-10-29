namespace Oscova.Agents.Host.Help;

public sealed record HelpTopicDescriptor(string RelativePath, string Icon = HelpTopicDescriptor.DefaultIcon)
{
    public const string DefaultIcon = "lucide:book-open";
}
