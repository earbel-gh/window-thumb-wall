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
    public WindowBounds? Bounds { get; set; }

    internal static WindowIdentity Capture(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);

        return new WindowIdentity
        {
            Handle = handle == IntPtr.Zero ? null : handle.ToInt64(),
            ProcessId = processId == 0 ? null : processId,
            ProcessStartTimeUtcTicks = GetProcessStartTimeUtcTicks(processId),
            ExecutablePath = NativeMethods.GetProcessImagePath(handle),
            WindowClassName = NativeMethods.GetWindowClassName(handle),
            AppUserModelId = NativeMethods.GetWindowAppUserModelId(handle),
            Bounds = CaptureBounds(handle)
        };
    }

    internal bool IsSameLiveProcessAndClass(IntPtr handle)
    {
        if (handle == IntPtr.Zero || Handle != handle.ToInt64())
            return false;

        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        if (processId == 0 || (ProcessId is uint savedProcessId && processId != savedProcessId))
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

    internal static WindowBounds? CaptureBounds(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect) ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return null;
        }

        return new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

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

internal sealed record WindowBounds
{
    public WindowBounds()
    {
    }

    public WindowBounds(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}
