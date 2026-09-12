using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Oscova.Agents.Tasks;

/// <summary>Validates supplied parameters against a task's external-trigger schema.</summary>
public static class TaskExternalTriggerValidator
{
    /// <summary>Validates and clones supplied parameters using the task's declared names and value kinds.</summary>
    public static IReadOnlyDictionary<string, JsonElement> ValidateParameters(
        AgentTaskDefinition task,
        IReadOnlyDictionary<string, JsonElement> supplied)
    {
        if (!task.ExternalTrigger.IsEnabled)
        {
            throw new TaskExternalTriggerValidationException(TaskExternalTriggerValidationFailureKind.TriggerDisabled);
        }

        var declared = task.ExternalTrigger.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter,
            StringComparer.OrdinalIgnoreCase);
        var validated = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var suppliedParameter in supplied)
        {
            if (!declared.TryGetValue(suppliedParameter.Key, out var parameter))
            {
                throw new TaskExternalTriggerValidationException(
                    TaskExternalTriggerValidationFailureKind.UnknownParameter,
                    suppliedParameter.Key);
            }

            if (!MatchesKind(suppliedParameter.Value, parameter.Kind))
            {
                throw new TaskExternalTriggerValidationException(
                    TaskExternalTriggerValidationFailureKind.ParameterKindMismatch,
                    parameter.Name,
                    parameter.Kind);
            }

            validated[parameter.Name] = suppliedParameter.Value.Clone();
        }

        foreach (var requiredParameter in declared.Values.Where(parameter => parameter.IsRequired))
        {
            if (!validated.ContainsKey(requiredParameter.Name))
            {
                throw new TaskExternalTriggerValidationException(
                    TaskExternalTriggerValidationFailureKind.RequiredParameterMissing,
                    requiredParameter.Name);
            }
        }

        return validated;
    }

    private static bool MatchesKind(JsonElement value, TaskExternalParameterKind kind)
    {
        return kind switch
        {
            TaskExternalParameterKind.String => value.ValueKind == JsonValueKind.String,
            TaskExternalParameterKind.Number => value.ValueKind == JsonValueKind.Number,
            TaskExternalParameterKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            TaskExternalParameterKind.Json => true,
            _ => false
        };
    }
}
