using System;

namespace Oscova.Agents.Speech;

/// <summary>Signals that a synthesis request targets a session that is not active.</summary>
public sealed class SpeechSynthesisSessionUnavailableException(string message) : InvalidOperationException(message);
