namespace Oscova.Agents.Hooks;

/// <summary>Marks an exception that must escape the function retry loop and abort the current agent run.</summary>
public interface IRunAbortException;