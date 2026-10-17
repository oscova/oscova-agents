namespace Oscova.Agents.Channels;

/// <summary>Carries a plugin resource key and neutral validation text for one field.</summary>
public sealed record ChannelValidationError(string Message, string? ResourceKey = null);
