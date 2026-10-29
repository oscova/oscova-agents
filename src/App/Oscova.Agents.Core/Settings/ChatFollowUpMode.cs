namespace Oscova.Agents.Core.Settings;

/// <summary>Chooses what a message sent during a live chat run does.</summary>
public enum ChatFollowUpMode
{
    /// <summary>Delivers the message to the running agent at its next model call.</summary>
    Steer,
    /// <summary>Holds the message and runs it as its own turn after the current one.</summary>
    Queue
}
