namespace Oscova.Agents.Core.ApiContracts.Environment;

/// <summary>Identifies the Host-owned environment shown before reset confirmation.</summary>
public sealed record EnvironmentResetPreview(string AppDataPath);

/// <summary>Confirms the operator's request to restart into an empty environment.</summary>
public sealed record ResetEnvironmentRequest(string Confirmation);

/// <summary>Reports accepted reset identity and the retained previous environment.</summary>
public sealed record EnvironmentResetAccepted(string OperationId, string BackupPath);

/// <summary>Reports whether this reset reached a committed fresh startup.</summary>
public sealed record EnvironmentResetCompletion(bool Completed);
