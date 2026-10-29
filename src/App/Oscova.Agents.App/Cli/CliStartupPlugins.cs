using Oscova.Agents.Host.Hosting;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Hardware;
using Oscova.Agents.Infrastructure.Hardware;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>Shares the optional-plugin checklist and edition selectors across startup modes.</summary>
public static class CliStartupPlugins
{
    /// <summary>Collects every choice before returning any optional installation decisions.</summary>
    public static async Task<IReadOnlyList<StartupPluginSelection>> SelectAsync(
        IAnsiConsole console, IReadOnlyList<StartupPluginOption> offered, CancellationToken cancellationToken)
    {
        console.Write(new Panel("Agent Templates · Web · Browser · MXC").Header("Automatic plugins"));
        var prompt = new MultiSelectionPrompt<StartupPluginOption>
        {
            Title = "[bold]Optional plugins[/] (add more later in Settings > Plugins)",
            Required = false,
            InstructionsText = "[grey]↑/↓ move · Space toggles · Enter continues · Esc cancels[/]",
            Converter = item => Markup.Escape($"{item.Name}: {item.Description}{item.Notice}"),
            CancelResult = () => throw new OperationCanceledException("Optional plugin setup cancelled.")
        };
        prompt.AddChoices(offered);
        var selected = await prompt.ShowAsync(console, cancellationToken).ConfigureAwait(false);
        var selections = new List<StartupPluginSelection>();
        using var logs = LoggerFactory.Create(builder => builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        using var supervisor = new DiagnosticProcessSupervisor(logs.CreateLogger<DiagnosticProcessSupervisor>());
        var probe = new HostHardwareProbe(supervisor, logs.CreateLogger<HostHardwareProbe>());
        HostHardwareSnapshot? hardware = null;
        foreach (var item in offered.Where(selected.Contains))
        {
            if (item.HardwareRequirement is not null || item.CudaHardwareRequirement is not null)
            {
                hardware ??= await probe.GetAsync(cancellationToken).ConfigureAwait(false);
                WriteAdvice(console, "Base", PluginHardwareAssessment.Assess(item.HardwareRequirement, hardware));
                if (item.SupportsCuda) { WriteAdvice(console, "NVIDIA CUDA", PluginHardwareAssessment.Assess(item.CudaHardwareRequirement, hardware)); }
            }

            var useCuda = false;
            if (item.SupportsCuda)
            {
                var edition = new SelectionPrompt<string>
                {
                    Title = Markup.Escape($"{item.Name} edition"),
                    Converter = value => value == "CPU" ? value : "NVIDIA CUDA (install NVIDIA runtime prerequisites separately)",
                    CancelResult = () => throw new OperationCanceledException("Optional plugin setup cancelled.")
                };
                edition.AddChoices("CPU", "NVIDIA CUDA");
                useCuda = await edition.ShowAsync(console, cancellationToken).ConfigureAwait(false) == "NVIDIA CUDA";
            }

            selections.Add(new StartupPluginSelection(item.PluginId, useCuda));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return selections;
    }

    private static void WriteAdvice(IAnsiConsole console, string edition, PluginHardwareAssessment assessment)
    {
        var requirement = assessment.Requirement;
        var declared = requirement is null ? "unknown requirements"
            : requirement.Backend == "cuda" ? $"CUDA {requirement.CudaMajor}, compatible NVIDIA GPU and driver"
            : requirement.Backend;
        var observation = assessment.Observation switch
        {
            "managed" => "placement is managed by the provider",
            "observed" when assessment.MatchingAdapters.Count > 0 => string.Join(", ", assessment.MatchingAdapters.Select(adapter => adapter.Name)),
            "observed" => "host CPU observed",
            "not-detected" => "matching hardware was not detected by this check; hardware or drivers may still be present",
            _ => "hardware check unavailable"
        };
        console.WriteLine($"{edition}: {declared}. {observation}. Runtime unverified; use the model provider Test action. Selection remains available.");
    }
}
