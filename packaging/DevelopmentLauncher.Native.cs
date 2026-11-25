#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowThumbWall.DevelopmentLauncher
{
    public sealed class DisplaySnapshot
    {
        public string DeviceName { get; set; } = string.Empty;
        public long MonitorHandle { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkRight { get; set; }
        public int WorkBottom { get; set; }
        public uint SystemDpi { get; set; }
        public uint TargetDpi { get; set; }
        public int WorkWidth => WorkRight - WorkLeft;
        public int WorkHeight => WorkBottom - WorkTop;
    }

    public static class NativeLauncher
    {
        private const uint StartfUseShowWindow = 0x00000001;
        private const short SwShowNoActivate = 4;
        private const uint CreateNoWindow = 0x08000000;
        private const uint MonitorDefaultToNull = 0;
        private const int DpiTypeEffective = 0;

        public static DisplaySnapshot ResolveDisplay(string exactDeviceName)
        {
            if (string.IsNullOrWhiteSpace(exactDeviceName))
                throw new ArgumentException("A GDI display device name is required.", nameof(exactDeviceName));

            IntPtr previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2.
            if (previousContext == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve display geometry in device pixels.");
            try
            {
                return ResolveDisplayInDevicePixels(exactDeviceName);
            }
            finally
            {
                SetThreadDpiAwarenessContext(previousContext);
            }
        }

        private static DisplaySnapshot ResolveDisplayInDevicePixels(string exactDeviceName)
        {
            DisplaySnapshot? match = null;
            string? callbackError = null;
            MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
                if (!GetMonitorInfo(monitor, ref info) ||
                    !string.Equals(info.DeviceName, exactDeviceName, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (match != null)
                {
                    callbackError = $"GDI returned more than one active monitor named '{exactDeviceName}'.";
                    return false;
                }

                uint systemDpi = GetDpiForSystem();
                if (systemDpi == 0)
                {
                    callbackError = "Windows did not return a system DPI.";
                    return false;
                }

                uint targetDpi = TryGetTargetDpi(monitor);
                match = new DisplaySnapshot
                {
                    DeviceName = info.DeviceName ?? string.Empty,
                    MonitorHandle = monitor.ToInt64(),
                    WorkLeft = info.Work.Left,
                    WorkTop = info.Work.Top,
                    WorkRight = info.Work.Right,
                    WorkBottom = info.Work.Bottom,
                    SystemDpi = systemDpi,
                    TargetDpi = targetDpi
                };
                return true;
            };

            bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (callbackError != null)
                throw new InvalidOperationException(callbackError);
            if (!enumerated)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate active GDI displays.");

            if (match == null)
                throw new InvalidOperationException($"The configured GDI display '{exactDeviceName}' is unavailable.");
            if (match.WorkWidth <= 0 || match.WorkHeight <= 0)
                throw new InvalidOperationException($"The active GDI display '{exactDeviceName}' has no usable work area.");
            return match;
        }

        public static void RequireTargetAvailable(DisplaySnapshot expected)
        {
            DisplaySnapshot current = ResolveDisplay(expected.DeviceName);
            if (current.MonitorHandle != expected.MonitorHandle ||
                current.WorkLeft != expected.WorkLeft || current.WorkTop != expected.WorkTop ||
                current.WorkRight != expected.WorkRight || current.WorkBottom != expected.WorkBottom ||
                current.SystemDpi != expected.SystemDpi || current.TargetDpi != expected.TargetDpi)
                throw new InvalidOperationException("The target GDI display or work area changed before launch.");

            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                throw new InvalidOperationException("The foreground window could not be verified.");
            IntPtr foregroundMonitor = MonitorFromWindow(foreground, MonitorDefaultToNull);
            if (foregroundMonitor == IntPtr.Zero)
                throw new InvalidOperationException("The foreground monitor could not be verified.");
            if (foregroundMonitor == new IntPtr(expected.MonitorHandle))
                throw new InvalidOperationException("The configured target display currently contains the foreground window.");
        }

        public static Process Launch(string executablePath)
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseShowWindow,
                ShowWindow = SwShowNoActivate
            };
            var commandLine = new StringBuilder("\"" + executablePath + "\"");
            if (!CreateProcess(executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateNoWindow, IntPtr.Zero, System.IO.Path.GetDirectoryName(executablePath),
                ref startup, out ProcessInformation child))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW could not start WindowThumbWall.");

            try
            {
                Process process = Process.GetProcessById(checked((int)child.ProcessId));
                _ = process.SafeHandle;
                return process;
            }
            finally
            {
                CloseHandle(child.Thread);
                CloseHandle(child.Process);
            }
        }

        private static uint TryGetTargetDpi(IntPtr monitor)
        {
            try
            {
                return GetDpiForMonitor(monitor, DpiTypeEffective, out uint x, out _) >= 0 ? x : 0;
            }
            catch (DllNotFoundException) { return 0; }
            catch (EntryPointNotFoundException) { return 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string? DeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr bounds, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("user32.dll", EntryPoint = "GetDpiForSystem")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
            ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
