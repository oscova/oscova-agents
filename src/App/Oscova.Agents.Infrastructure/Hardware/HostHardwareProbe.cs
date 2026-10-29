using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Oscova.Agents.Core.Hardware;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Shares bounded passive observations, caching failures for five minutes too.</summary>
public sealed class HostHardwareProbe(DiagnosticProcessSupervisor supervisor, ILogger<HostHardwareProbe> logger)
{
    private readonly Lock _gate = new();
    private Task<HostHardwareSnapshot>? _pending;
    private DateTimeOffset _expires;

    /// <summary>Returns observations without allowing one caller to cancel another caller's discovery.</summary>
    public Task<HostHardwareSnapshot> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_pending is null || (_pending.IsCompleted && DateTimeOffset.UtcNow >= _expires))
            {
                _expires = DateTimeOffset.UtcNow.AddMinutes(5);
                _pending = ObserveAsync();
            }

            return _pending.WaitAsync(ct);
        }
    }

    private async Task<HostHardwareSnapshot> ObserveAsync()
    {
        var budget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var result = await supervisor.RunAsync(new("passive", 0, 0, default, budget),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
        if (result.Response?.Hardware is { } hardware) { return hardware; }

        logger.LogWarning("Hardware discovery unavailable: {Reason}; cleanup confirmed: {CleanupConfirmed}.", result.Reason, result.CleanupConfirmed);
        return new(DateTimeOffset.UtcNow, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, null, budget > 0 ? budget : null, [], false, false);
    }

    internal static async Task<HostHardwareSnapshot> CollectAsync(long? processBudget, CancellationToken ct)
    {
        var adapters = new List<HostGraphicsAdapter>();
        long? physical = null;
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical <= long.MaxValue) { physical = (long)memory.TotalPhysical; }

            try { ReadWindowsAdapters(adapters); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { Console.Error.WriteLine($"Display registry discovery unavailable: {ex.Message}"); }
        }
        else if (OperatingSystem.IsLinux())
        {
            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(value => value.StartsWith("MemTotal:", StringComparison.Ordinal));
            var value = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (value?.Length == 3 && value[2] == "kB") { physical = ParseBytes(value[1], 1024); }

            try { ReadLinuxAdapters(adapters); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            { Console.Error.WriteLine($"DRM discovery unavailable: {ex.Message}"); }
        }
        else if (OperatingSystem.IsMacOS())
        {
            physical = ParseBytes(await ReadCommandAsync("sysctl", ["-n", "hw.memsize"], ct).ConfigureAwait(false), 1);
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            { adapters.Add(new("platform", "apple-silicon", "Apple", "Apple Silicon", null, true)); }
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            var nvidia = await ReadCommandAsync("nvidia-smi",
                ["--query-gpu=uuid,pci.bus_id,name,memory.total,driver_version", "--format=csv,noheader,nounits"], ct).ConfigureAwait(false);
            var detected = ParseNvidia(nvidia);
            if (detected.Count > 0)
            {
                adapters.RemoveAll(adapter => adapter.Vendor == "NVIDIA");
                adapters.AddRange(detected);
            }
        }

        return new(DateTimeOffset.UtcNow, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, physical, processBudget, adapters, true,
            Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") is null &&
            Environment.GetEnvironmentVariable("CUDA_DEVICE_ORDER") is null);
    }

    internal static IReadOnlyList<HostGraphicsAdapter> ParseNvidia(string? output)
    {
        var result = new List<HostGraphicsAdapter>();
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            // A partial inventory must not turn a multi-device host into an apparently unambiguous device.
            if (fields.Length != 5 || fields.Any(string.IsNullOrWhiteSpace) || !fields[0].StartsWith("GPU-", StringComparison.Ordinal)) { return []; }

            result.Add(new("nvidia-smi", fields[0] + "/" + fields[1], "NVIDIA", fields[2], ParseBytes(fields[3], 1024 * 1024), false, fields[4]));
        }

        return result;
    }

    private static long? ParseBytes(string? value, long multiplier) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
        number > 0 && number <= long.MaxValue / multiplier ? number * multiplier : null;

    [SupportedOSPlatform("windows")]
    private static void ReadWindowsAdapters(List<HostGraphicsAdapter> adapters)
    {
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (display is null) { return; }

        foreach (var name in display.GetSubKeyNames())
        {
            try
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is not string label) { continue; }

                var memory = adapter.GetValue("HardwareInformation.qwMemorySize") as long?;
                adapters.Add(new("registry", adapter.Name, label.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" : null,
                    label, memory > 0 ? memory : null, null));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { Console.Error.WriteLine($"Display registry entry {name} unavailable: {ex.Message}"); }
        }
    }

    private static void ReadLinuxAdapters(List<HostGraphicsAdapter> adapters)
    {
        if (!Directory.Exists("/sys/class/drm")) { return; }

        foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
        {
            try
            {
                var memoryFile = Path.Combine(card, "device", "mem_info_vram_total");
                if (!File.Exists(memoryFile)) { continue; }

                var vendorFile = Path.Combine(card, "device", "vendor");
                var vendor = File.Exists(vendorFile) ? File.ReadAllText(vendorFile).Trim() : null;
                adapters.Add(new("drm", Path.GetFileName(card), vendor == "0x10de" ? "NVIDIA" : vendor,
                    Path.GetFileName(card), ParseBytes(File.ReadAllText(memoryFile), 1), null));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            { Console.Error.WriteLine($"DRM entry {Path.GetFileName(card)} unavailable: {ex.Message}"); }
        }
    }

    private static async Task<string?> ReadCommandAsync(string command, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var process = new Process { StartInfo = new(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) { process.StartInfo.ArgumentList.Add(argument); }

        try
        {
            await LocalDiagnosticWorker.StartPassiveCommandAsync(process, timeout.Token).ConfigureAwait(false);
            var output = LocalDiagnosticWorker.ReadBoundedAsync(process.StandardOutput, 32 * 1024, timeout.Token);
            var error = LocalDiagnosticWorker.ReadBoundedAsync(process.StandardError, 8 * 1024, timeout.Token, truncate: true);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException) { return null; }
        finally
        {
            if (!await LocalDiagnosticWorker.StopPassiveCommandAsync().ConfigureAwait(false))
            {
                // Keep the worker alive so its parent retains the unconfirmed process tree.
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
