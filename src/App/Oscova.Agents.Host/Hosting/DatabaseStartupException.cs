namespace Oscova.Agents.Host.Hosting;

/// <summary>Carries a safe database startup diagnosis to the CLI while retaining private failure details.</summary>
public sealed class DatabaseStartupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
