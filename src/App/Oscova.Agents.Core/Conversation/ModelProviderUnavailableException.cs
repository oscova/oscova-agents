namespace Oscova.Agents.Core.Conversation;

/// <summary>Reports that no configured model provider could initialize a client for the requested operation.</summary>
public sealed class ModelProviderUnavailableException : InvalidOperationException
{
    public ModelProviderUnavailableException(string message)
        : base(message)
    {
    }

    public ModelProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
