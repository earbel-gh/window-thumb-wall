using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WindowThumbWall;

internal sealed class AppState
{
    internal const string StatePathOverrideEnvironmentVariable = "WINDOWTHUMBWALL_STATE_PATH";

    public List<SlotState> Slots { get; set; } = [];
    public List<string> AutoAddApps { get; set; } = [];
    public WindowGeometry? Geometry { get; set; }
    public bool IsFullScreen { get; set; }
    public double LeftPanelWidth { get; set; }
    public double AppListHeight { get; set; }
    public bool EnableOsNotificationAttention { get; set; }

    internal static AppState Load() => Load(out _);

    internal static AppState Load(out bool canSave)
    {
        string statePath = GetStatePath();
        canSave = true;
        try
        {
            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<AppState>(json);
            if (state?.Slots == null || state.AutoAddApps == null || state.Slots.Any(slot => slot == null))
                throw new JsonException("WindowThumbWall state has an invalid layout.");
            return state;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            canSave = false;
            Trace.TraceWarning("Could not load WindowThumbWall state: {0}", ex.GetType().Name);
            return new AppState();
        }
    }

    internal void Save()
    {
        string statePath = GetStatePath();
        string tempPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not save WindowThumbWall state: {0}", ex.GetType().Name);
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not remove temporary WindowThumbWall state: {0}", cleanupError.GetType().Name);
            }
        }
    }

    internal static string GetStatePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(StatePathOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowThumbWall", "state.json");
    }
}

internal sealed class SlotState
{
    public string ProcessName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public WindowIdentity? Identity { get; set; }
}

internal sealed class WindowGeometry
{
    internal static WindowGeometry? FromBounds(double left, double top, double width, double height, bool isMaximized) =>
        double.IsFinite(left) && double.IsFinite(top) &&
        double.IsFinite(width) && width > 0 && double.IsFinite(height) && height > 0
            ? new WindowGeometry { Left = left, Top = top, Width = width, Height = height, IsMaximized = isMaximized }
            : null;

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}
