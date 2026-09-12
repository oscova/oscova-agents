using System.Collections.Generic;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Configures the externally callable webhook trigger for a task.
    /// </summary>
    public sealed class TaskExternalTrigger
    {
        /// <summary>
        /// Gets or sets whether external callers may trigger this task.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets or sets the system-wide unique URL slug for this task trigger.
        /// </summary>
        public string Slug { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the allowed parameter definitions for webhook callers.
        /// </summary>
        public IList<TaskExternalParameter> Parameters { get; set; } = [];
    }

    /// <summary>
    /// Describes one allowed external trigger parameter.
    /// </summary>
    public sealed class TaskExternalParameter
    {
        /// <summary>
        /// Gets or sets the parameter name used in JSON and instruction placeholders.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the expected JSON value kind.
        /// </summary>
        public TaskExternalParameterKind Kind { get; set; } = TaskExternalParameterKind.String;

        /// <summary>
        /// Gets or sets whether the caller must provide the parameter.
        /// </summary>
        public bool IsRequired { get; set; }
    }

    /// <summary>
    /// Supported value kinds for task webhook parameters.
    /// </summary>
    public enum TaskExternalParameterKind
    {
        /// <summary>
        /// A JSON string value.
        /// </summary>
        String = 0,

        /// <summary>
        /// A JSON numeric value.
        /// </summary>
        Number = 1,

        /// <summary>
        /// A JSON boolean value.
        /// </summary>
        Boolean = 2,

        /// <summary>
        /// Any JSON value.
        /// </summary>
        Json = 3
    }
}
