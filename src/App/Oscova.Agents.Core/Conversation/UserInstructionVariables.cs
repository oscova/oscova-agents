namespace Oscova.Agents.Core.Conversation;

public sealed record UserInstructionVariables(
    string UserName,
    string UserNickname,
    string UserUserName,
    string UserEmail,
    TimeZoneInfo TimeZone);
