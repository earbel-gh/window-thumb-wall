using System.ComponentModel;
using System.Diagnostics;

namespace WindowThumbWall;

internal sealed class WindowIdentity
{
    public long? Handle { get; set; }
    public uint? ProcessId { get; set; }
    public long? ProcessStartTimeUtcTicks { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string WindowClassName { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;

    internal static WindowIdentity Capture(IntPtr handle)
    {
        WindowIdentity identity = CaptureLive(handle);
        identity.ExecutablePath = NativeMethods.GetProcessImagePath(handle);
        identity.AppUserModelId = NativeMethods.GetWindowAppUserModelId(handle);
        return identity;
    }

    internal static WindowIdentity CaptureLive(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);

        return new WindowIdentity
        {
            Handle = handle == IntPtr.Zero ? null : handle.ToInt64(),
            ProcessId = processId == 0 ? null : processId,
            ProcessStartTimeUtcTicks = GetProcessStartTimeUtcTicks(processId),
            WindowClassName = NativeMethods.GetWindowClassName(handle)
        };
    }

    internal bool IsSameLiveProcessAndClass(IntPtr handle)
    {
        if (handle == IntPtr.Zero || Handle != handle.ToInt64())
            return false;

        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        if (processId == 0 || ProcessId != processId)
            return false;

        if (!string.IsNullOrWhiteSpace(WindowClassName) &&
            !string.Equals(WindowClassName, NativeMethods.GetWindowClassName(handle), StringComparison.Ordinal))
        {
            return false;
        }

        if (ProcessStartTimeUtcTicks is long savedStartTime)
            return GetProcessStartTimeUtcTicks(processId) == savedStartTime;

        return true;
    }

    internal WindowIdentity WithoutHandle() => new()
    {
        ProcessId = ProcessId,
        ProcessStartTimeUtcTicks = ProcessStartTimeUtcTicks,
        ExecutablePath = ExecutablePath,
        WindowClassName = WindowClassName,
        AppUserModelId = AppUserModelId
    };

    private static long? GetProcessStartTimeUtcTicks(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
            return null;

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
