namespace Oscova.Agents.Core.Localization;

/// <summary>
/// Resource keys for background-emitted notification strings shared with Infrastructure.
/// </summary>
public static class NotificationTextKeys
{
    /// <summary>Names recovery guidance when saved channel configuration cannot be applied.</summary>
    public const string ChannelSavedNotApplied = "Notifications.Channels.SavedNotApplied";
    public const string ModelBudgetOwnerExceeded = "ModelBudgetOwnerExceeded";
    public const string ModelBudgetSharedExceeded = "ModelBudgetSharedExceeded";
    public const string ModelBudgetContinue = "ModelBudgetContinue";
    public const string ModelBudgetTaskSkipped = "ModelBudgetTaskSkipped";
    public const string ModelBudgetSearchPaused = "ModelBudgetSearchPaused";
    public const string ModelBudgetThreshold = "ModelBudgetThreshold";
    public const string PluginSetupTitle = "Notifications.Plugins.SetupTitle";
    public const string PluginSetupBody = "Notifications.Plugins.SetupBody";
    public const string PluginDeviceUnavailable = "Notifications.Plugins.DeviceUnavailable";
    public const string ExecutionInterruptedTitle = "Notifications.Execution.InterruptedTitle";
    public const string ExecutionInterruptedBody = "Notifications.Execution.InterruptedBody";

    public const string ProviderAttentionTitle = "Notifications.Providers.AttentionTitle";
    public const string ProviderAttentionBody = "Notifications.Providers.AttentionBody";
    public const string TaskCompletedTitle = "Notifications.Task.CompletedTitle";
    public const string TaskNeedsAttentionTitle = "Notifications.Task.NeedsAttentionTitle";
    public const string TaskCompletedInSeconds = "Notifications.Task.CompletedInSeconds";
    public const string TaskSkipped = "Notifications.Task.Skipped";
    public const string TaskMissingChatProvider = "Notifications.Task.MissingChatProvider";
    public const string TaskMissingEmbeddingProvider = "Notifications.Task.MissingEmbeddingProvider";
    public const string TaskHandlerUnavailable = "Notifications.Task.HandlerUnavailable";
    public const string TaskStepLimitReached = "Notifications.Task.StepLimitReached";
    public const string TaskRunFinished = "Notifications.Task.Finished";
    public const string TaskOutcomeHidden = "Notifications.Task.OutcomeHidden";
    public const string TaskSkippedLocked = "Notifications.Task.SkippedLocked";
    public const string TaskSkippedLowBattery = "Notifications.Task.SkippedLowBattery";
    public const string TaskBatteryInformationUnavailable = "Notifications.Task.BatteryInformationUnavailable";
}
