using System;

namespace Oscova.Agents.Speech;

/// <summary>Signals that a speech provider cannot run until its model is available.</summary>
public sealed class SpeechModelUnavailableException(string message) : InvalidOperationException(message);
