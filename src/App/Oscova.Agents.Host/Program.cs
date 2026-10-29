using Oscova.Agents.Host;
using Oscova.Agents.Infrastructure.Hardware;

if (await LocalDiagnosticWorker.TryRunAsync(args).ConfigureAwait(false)) { return; }

await WebHostStartup.RunAsync(args).ConfigureAwait(false);

public partial class Program;
