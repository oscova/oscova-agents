using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Plugins;

/// <summary>Shares a model draft with plugin controls while the host retains validation and atomic persistence.</summary>
public sealed record PluginModelProviderSettingsContext(ModelProviderConfig Draft,
    IReadOnlyDictionary<string, string> FieldErrors, Func<Task> ChangedAsync);
