using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Captures the persisted and live state of one task execution.
    /// </summary>
    public class AgentTaskRun
    {
        private readonly Lock _liveMessagesLock = new();

        /// <summary>
        /// Gets or sets the stable run identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the task definition identifier this run belongs to.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets the user who owned the task when this run started.
        /// </summary>
        [JsonIgnore]
        public string UserId { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the current run status.
        /// </summary>
        public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;

        /// <summary>
        /// Gets or sets when execution started.
        /// </summary>
        public DateTimeOffset? StartedAt { get; set; }

        /// <summary>
        /// Gets or sets when execution completed or failed.
        /// </summary>
        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>
        /// Gets or sets the final human-readable result summary.
        /// </summary>
        public string? ResultSummary { get; set; }

        /// <summary>
        /// Gets or sets serialized session state captured for persistent or handoff scenarios.
        /// </summary>
        public JsonElement? SessionElement { get; set; }

        /// <summary>
        /// Gets or sets the failure message when the run fails.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets the token usage recorded for the run when available.
        /// </summary>
        public int? TokensUsed { get; set; }

        /// <summary>
        /// Gets or sets how this run was started.
        /// </summary>
        public TaskTriggerSource TriggerSource { get; set; } = TaskTriggerSource.Schedule;

        /// <summary>Gets or sets whether this run's content is stored encrypted.</summary>
        public bool IsEncrypted { get; set; }

        /// <summary>
        /// Gets or sets validated webhook parameters captured for this run.
        /// </summary>
        public IDictionary<string, JsonElement> TriggerParameters { get; set; } = new Dictionary<string, JsonElement>();

        /// <summary>
        /// Gets or sets the system instruction captured from the first model request for this run.
        /// </summary>
        public string? SystemInstruction { get; set; }

        /// <summary>
        /// Accumulates chat messages during and after an active run. Populated by the
        /// observer middleware during execution. Persistence and rehydration of these
        /// messages is handled by the configured task store implementation, so they are
        /// available both for live runs and for completed runs loaded from storage.
        /// The store owns disk persistence via its own serialized messages element; this
        /// property is carried over the management API so the session inspector can read it.
        /// </summary>
        public ICollection<ChatMessage>? LiveMessages { get; set; }

        /// <summary>
        /// Appends a single message to <see cref="LiveMessages"/> under a lock.
        /// </summary>
        public void AddLiveMessage(ChatMessage message)
        {
            lock (_liveMessagesLock)
            {
                LiveMessages?.Add(message);
            }
        }

        /// <summary>
        /// Appends multiple messages to <see cref="LiveMessages"/> under a lock.
        /// </summary>
        public void AddLiveMessages(IEnumerable<ChatMessage> messages)
        {
            lock (_liveMessagesLock)
            {
                if (LiveMessages is null)
                {
                    return;
                }

                foreach (ChatMessage message in messages)
                {
                    LiveMessages.Add(message);
                }
            }
        }

        /// <summary>
        /// Returns a point-in-time snapshot of <see cref="LiveMessages"/> as an array, or
        /// <see langword="null"/> if <see cref="LiveMessages"/> has not been initialised.
        /// Safe to call from any thread.
        /// </summary>
        public ChatMessage[]? GetLiveMessagesSnapshot()
        {
            lock (_liveMessagesLock)
            {
                return LiveMessages?.ToArray();
            }
        }

        /// <summary>Invoked after captured request messages are appended and after each non-streaming response is observed.</summary>
        /// <remarks>
        /// Callbacks run synchronously on the invoking thread. Callers own thread marshalling, and subscriber exceptions propagate.
        /// </remarks>
        [JsonIgnore]
        public Action? LiveMessagesChanged { get; set; }
    }
}
