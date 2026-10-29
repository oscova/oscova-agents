using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Oscova.Agents.Infrastructure.Utils;

/// <summary>Applies process policy before native plugin runtimes initialize.</summary>
public static class NativeRuntimeEnvironment
{
    private const string OnnxTelemetryVariable = "ORT_DISABLE_TELEMETRY";

    /// <summary>Prevents ONNX telemetry uploaders from starting in this process and its children.</summary>
    public static void DisableOnnxTelemetry()
    {
        // Unix native libraries read libc's environment rather than .NET's managed copy.
        if (!OperatingSystem.IsWindows() && SetUnixEnvironmentVariable(OnnxTelemetryVariable, "1", 1) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not disable ONNX Runtime telemetry.");
        }

        // The uploader can block DLL shutdown while closing its network session.
        Environment.SetEnvironmentVariable(OnnxTelemetryVariable, "1");
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetUnixEnvironmentVariable(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int overwrite);
}
