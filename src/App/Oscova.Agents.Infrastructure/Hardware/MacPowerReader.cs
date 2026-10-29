using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Hardware;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Copies and interprets public macOS internal battery descriptions.</summary>
internal static class MacPowerReader
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;

    /// <summary>Holds copied source values independently of native object lifetimes.</summary>
    internal sealed record Source(string? Type, bool? Present, double? Current,
        double? Full, string? PowerState);

    /// <summary>Reads fresh public power sources while owning their copied native references.</summary>
    internal static HostPowerSnapshot Read(ILogger logger)
    {
        var info = IOPSCopyPowerSourcesInfo();
        if (info == 0)
        {
            logger.LogWarning("IOPSCopyPowerSourcesInfo could not read host power state");
            return new(null, null, null);
        }

        nint list = 0;
        try
        {
            list = IOPSCopyPowerSourcesList(info);
            if (list == 0)
            {
                logger.LogWarning("IOPSCopyPowerSourcesList could not enumerate host power sources");
                return new(null, null, null);
            }

            var sources = new List<Source>();
            var count = CFArrayGetCount(list);
            for (nint index = 0; index < count; index++)
            {
                // Descriptions are borrowed from info and remain valid until its release.
                var description = IOPSGetPowerSourceDescription(info, CFArrayGetValueAtIndex(list, index));
                sources.Add(new(String(description, "Type"), Boolean(description, "Is Present"),
                    Number(description, "Current Capacity"), Number(description, "Max Capacity"),
                    String(description, "Power Source State")));
            }

            return Decode(sources);
        }
        finally
        {
            if (list != 0) { CFRelease(list); }

            CFRelease(info);
        }
    }

    /// <summary>Preserves incomplete sources and avoids treating source percentages as energy.</summary>
    internal static HostPowerSnapshot Decode(IReadOnlyList<Source> sources)
    {
        var incomplete = sources.Any(source => source.Type is null
            || source.Type == "InternalBattery" && source.Present is null);
        var batteries = sources.Where(source => source.Type == "InternalBattery"
            && source.Present == true).ToArray();
        bool? hasBattery = batteries.Length > 0 ? true : incomplete ? null : false;
        double? percentage = !incomplete && batteries.Length == 1
            ? HostPowerService.Percentage(batteries[0].Current, batteries[0].Full) : null;
        bool? connected = !incomplete && batteries.Length > 0
            && batteries.All(source => source.PowerState == "AC Power") ? true
            : !incomplete && batteries.Length > 0
                && batteries.All(source => source.PowerState == "Battery Power") ? false : null;
        return new(hasBattery, percentage, connected);
    }

    /// <summary>Looks up a borrowed dictionary value using an owned temporary key.</summary>
    private static nint Value(nint dictionary, string key)
    {
        if (dictionary == 0) { return 0; }

        var nativeKey = CFStringCreateWithCString(0, key, Utf8);
        if (nativeKey == 0) { return 0; }

        try { return CFDictionaryGetValue(dictionary, nativeKey); }
        finally { CFRelease(nativeKey); }
    }

    private static string? String(nint dictionary, string key)
    {
        var value = Value(dictionary, key);
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) { return null; }

        var buffer = new byte[128];
        if (!CFStringGetCString(value, buffer, buffer.Length, Utf8)) { return null; }

        return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
    }

    private static bool? Boolean(nint dictionary, string key)
    {
        var value = Value(dictionary, key);
        return value != 0 && CFGetTypeID(value) == CFBooleanGetTypeID()
            ? CFBooleanGetValue(value) : null;
    }

    private static double? Number(nint dictionary, string key)
    {
        var value = Value(dictionary, key);
        // CFNumberDoubleType is 13 and reports whether the conversion succeeds.
        return value != 0 && CFGetTypeID(value) == CFNumberGetTypeID()
            && CFNumberGetValue(value, 13, out var number) ? number : null;
    }

    [DllImport(IOKit)] private static extern nint IOPSCopyPowerSourcesInfo();
    [DllImport(IOKit)] private static extern nint IOPSCopyPowerSourcesList(nint info);
    [DllImport(IOKit)] private static extern nint IOPSGetPowerSourceDescription(nint info, nint source);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFBooleanGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(nint value);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(nint value, nint type, out double number);
}
