namespace Oscova.Agents.Host.Help;

// Scoped (per-circuit) link between trigger buttons and the single HelpPanel instance.
public sealed class HelpPanelHost
{
    private Func<string, Task>? _opener;

    public void Register(Func<string, Task> opener) => _opener = opener;
    public void Unregister() => _opener = null;

    public Task OpenAsync(string topicKey)
        => _opener is null ? Task.CompletedTask : _opener(topicKey);
}
