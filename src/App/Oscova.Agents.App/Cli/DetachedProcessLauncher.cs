using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Oscova.Agents.App.Cli;

/// <summary>Shares native detached process launch mechanics between CLI services.</summary>
public static class DetachedProcessLauncher
{
    /// <summary>
    /// Shell prefix that keeps a Unix command alive when its terminal closes.
    /// setsid gives the command its own session without forking (it is not a group leader), so it keeps its pid.
    /// macOS has no setsid command, so nohup ignores the hang-up there instead.
    /// </summary>
    internal const string UnixHangupShield = "\"$(command -v setsid || echo nohup)\"";

    /// <summary>Starts this executable detached, including its entry assembly when launched through dotnet.</summary>
    public static async Task<Process?> LaunchAsync(IReadOnlyList<string> childArgs, CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath ?? throw new CliExitException(1, "Could not find the Oscova executable path.");
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assemblyPath)) throw new CliExitException(1, "Could not find the Oscova entry assembly.");
            childArgs = new[] { assemblyPath }.Concat(childArgs).ToArray();
        }

        return await LaunchAsync(processPath, childArgs, Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts an explicit executable detached without inserting the current entry assembly.</summary>
    public static async Task<Process?> LaunchAsync(
        string executablePath,
        IReadOnlyList<string> childArgs,
        string workingDirectory,
        CancellationToken cancellationToken,
        Action? started = null)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
        {
            throw new CliExitException(1, "Could not find the executable to start in the background.");
        }

        if (OperatingSystem.IsWindows())
        {
            return StartDetachedWindowsProcess(executablePath, childArgs, workingDirectory, started, DetachedProcess);
        }

        return await StartDetachedUnixProcessAsync(executablePath, childArgs, workingDirectory, cancellationToken, started, "/dev/null").ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an explicit executable in its own visible terminal window, independent of this process.
    /// Without a desktop terminal it starts in the background with its output in <paramref name="fallbackOutputPath"/>.
    /// Returns true when a terminal window was opened.
    /// </summary>
    public static async Task<bool> LaunchInConsoleAsync(
        string executablePath,
        IReadOnlyList<string> childArgs,
        string workingDirectory,
        string fallbackOutputPath,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
        {
            throw new CliExitException(1, "Could not find the executable to start in a terminal.");
        }

        if (OperatingSystem.IsWindows())
        {
            StartDetachedWindowsProcess(executablePath, childArgs, workingDirectory, null, CreateNewConsole).Dispose();
            return true;
        }

        var command = new[] { executablePath }.Concat(childArgs).ToArray();
        if (OperatingSystem.IsMacOS() &&
            await TryOpenMacTerminalAsync(command, workingDirectory, Path.ChangeExtension(fallbackOutputPath, ".command"), cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (OperatingSystem.IsLinux() && FindLinuxTerminal(command) is { } terminal)
        {
            (await StartDetachedUnixProcessAsync(terminal.Executable, terminal.Arguments, workingDirectory, cancellationToken, null, "/dev/null")
                .ConfigureAwait(false)).Dispose();
            return true;
        }

        (await StartDetachedUnixProcessAsync(executablePath, childArgs, workingDirectory, cancellationToken, null, fallbackOutputPath)
            .ConfigureAwait(false)).Dispose();
        return false;
    }

    private static (string Executable, string[] Arguments)? FindLinuxTerminal(string[] command)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return null;
        }

        if (FindOnPath("x-terminal-emulator") is { } debianTerminal) { return (debianTerminal, ["-e", .. command]); }

        if (FindOnPath("gnome-terminal") is { } gnomeTerminal) { return (gnomeTerminal, ["--", .. command]); }

        return null;
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    // Terminal keeps a window open after its shell exits, so success closes it once the shell has exited, waiting up to 10 seconds.
    // Closing while zsh saves its session asks to terminate processes. A window holding other tabs, or a failure, stays open.
    // The braces keep `&` on the closer alone. Terminal controlling itself needs no Automation permission.
    private const string CloseMacTerminalWindow =
        "{ osascript -e 'on run argv' -e 'tell application \"Terminal\"' -e 'repeat 100 times' -e 'delay 0.1' " +
        "-e 'repeat with w in windows' " +
        "-e 'if (count of tabs of w) is 1 and tty of selected tab of w is (item 1 of argv) and (count of processes of selected tab of w) is 0 then' " +
        "-e 'close w' -e 'return' -e 'end if' -e 'end repeat' -e 'end repeat' -e 'end tell' -e 'end run' \"$(tty)\" >/dev/null 2>&1 & }";

    // Opening a .command file goes through Launch Services, so macOS asks no Automation permission, unlike AppleScript `do script`.
    // Terminal starts the script in a fresh login shell, so the script carries this process's environment, as a Windows or Linux console inherits it.
    // Only the variables Terminal sets for its own session are left to Terminal.
    // The script deletes itself when it starts, because it carries those settings.
    [SupportedOSPlatform("macos")]
    private static async Task<bool> TryOpenMacTerminalAsync(
        string[] command,
        string workingDirectory,
        string scriptPath,
        CancellationToken cancellationToken)
    {
        string[] terminalSession = ["SHLVL", "PWD", "OLDPWD", "_"];
        var settings = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => !name.StartsWith("TERM", StringComparison.Ordinal) && !terminalSession.Contains(name))
            .Select(name => QuoteForUnixShell($"{name}={Environment.GetEnvironmentVariable(name)}"));
        var script = $"#!/bin/sh\nrm -f \"$0\"\ncd {QuoteForUnixShell(workingDirectory)} && env {string.Join(' ', settings.Concat(command.Select(QuoteForUnixShell)))} && {CloseMacTerminalWindow}\n";
        await File.WriteAllTextAsync(scriptPath, script, cancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var startInfo = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add("Terminal");
        startInfo.ArgumentList.Add(scriptPath);
        var opened = false;
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) { return false; }

            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            opened = process.ExitCode == 0;
            return opened;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            return false;
        }
        finally
        {
            // Without a window the update runs in the background, so a late Terminal start must find no script to run twice.
            if (!opened) { File.Delete(scriptPath); }
        }
    }

    private static async Task<Process> StartDetachedUnixProcessAsync(
        string executablePath,
        IReadOnlyList<string> childArgs,
        string workingDirectory,
        CancellationToken cancellationToken,
        Action? started,
        string outputPath)
    {
        var startInfo = CreateUnixStartInfo(executablePath, childArgs, workingDirectory, outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo) ?? throw new CliExitException(1, "Could not start Oscova in the background.");
        started?.Invoke();
        // Capture the child before returning to the caller's readiness and cancellation checks.
        var processId = int.Parse(
            await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)
                ?? throw new CliExitException(1, "Could not identify the background process."),
            System.Globalization.CultureInfo.InvariantCulture);
        var child = Process.GetProcessById(processId);
        try
        {
            _ = child.SafeHandle;
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new CliExitException(1, "Could not start Oscova in the background.");
            }

            return child;
        }
        catch
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
            finally
            {
                child.Dispose();
            }

            throw;
        }
    }

    private static Process StartDetachedWindowsProcess(
        string processPath,
        IReadOnlyList<string> childArgs,
        string workingDirectory,
        Action? started,
        int consoleFlag)
    {
        var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder(string.Join(' ', new[] { processPath }.Concat(childArgs).Select(QuoteForWindowsProcess)));
        // Inherit neither the terminal's job nor its handles, so closing SSH cannot stop or retain the session.
        if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                consoleFlag | CreateNewProcessGroup | CreateBreakawayFromJob,
                IntPtr.Zero, workingDirectory, ref startupInfo, out var processInformation))
        {
            throw new CliExitException(1, $"Could not start Oscova in the background: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        try
        {
            started?.Invoke();
            var process = Process.GetProcessById(processInformation.dwProcessId);
            try
            {
                // Retain identity before closing CreateProcess's handle, so PID reuse cannot retarget cleanup.
                _ = process.SafeHandle;
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        finally
        {
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
        }
    }

    private static ProcessStartInfo CreateUnixStartInfo(
        string processPath,
        IReadOnlyList<string> childArgs,
        string workingDirectory,
        string outputPath)
    {
        // A plain background job stays in the caller's terminal session, so closing that terminal hangs it up.
        var command = UnixHangupShield + " " +
            string.Join(' ', new[] { processPath }.Concat(childArgs).Select(QuoteForUnixShell)) +
            $" </dev/null >>{QuoteForUnixShell(outputPath)} 2>&1 & echo $!";
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static string QuoteForUnixShell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string QuoteForWindowsProcess(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1)
                    .Append(character);
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount)
                .Append(character);
            backslashCount = 0;
        }

        builder.Append('\\', backslashCount * 2)
            .Append('"');
        return builder.ToString();
    }

    private const int DetachedProcess = 0x00000008;
    private const int CreateNewConsole = 0x00000010;
    private const int CreateNewProcessGroup = 0x00000200;
    private const int CreateBreakawayFromJob = 0x01000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
