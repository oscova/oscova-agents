using System;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Customizations an agent task handler applies around the standard agent run.
/// </summary>
/// <param name="Instruction">Replaces the raw task instruction for this run.</param>
/// <param name="OnSuccessAsync">
/// Optional callback invoked after the agent run succeeds and before the run is marked completed.
/// If this throws, the run is marked failed.
/// </param>
/// <param name="FinalizeAsync">Optionally selects input and success progress after the executor supplies its evidence token allowance.</param>
public sealed record AgentRunPlan(
    string Instruction,
    Func<CancellationToken, Task>? OnSuccessAsync = null,
    Func<int, CancellationToken, Task<AgentRunPlan>>? FinalizeAsync = null);
