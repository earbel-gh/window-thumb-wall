using System.IO;

namespace WindowThumbWall.Tests;

public sealed class AppStateTests : IDisposable
{
    private readonly string? _originalOverridePath;
    private readonly string _tempDirectory;
    private readonly string _statePath;

    public AppStateTests()
    {
        _originalOverridePath = Environment.GetEnvironmentVariable(AppState.StatePathOverrideEnvironmentVariable);
        _tempDirectory = Path.Combine(Path.GetTempPath(), "WindowThumbWall.Tests", Guid.NewGuid().ToString("N"));
        _statePath = Path.Combine(_tempDirectory, "state.json");
        Environment.SetEnvironmentVariable(AppState.StatePathOverrideEnvironmentVariable, _statePath);
    }

    [Fact]
    public void GetStatePath_UsesOverrideEnvironmentVariable()
    {
        string resolvedPath = AppState.GetStatePath();

        Assert.Equal(_statePath, resolvedPath);
    }

    [Fact]
    public void SaveAndLoad_UseOverriddenPath()
    {
        AppState state = new()
        {
            AutoAddApps = ["chrome", "outlook"],
            LeftPanelWidth = 320,
            AppListHeight = 240,
            EnableOsNotificationAttention = true,
            Slots =
            [
                new SlotState
                {
                    ProcessName = "notepad",
                    Title = "notes.txt",
                    IsPinned = true,
                    Identity = new WindowIdentity
                    {
                        Handle = 123,
                        ProcessId = 456,
                        ProcessStartTimeUtcTicks = 789,
                        ExecutablePath = @"C:\Windows\notepad.exe",
                        WindowClassName = "Notepad",
                        AppUserModelId = "notepad.app"
                    }
                }
            ]
        };

        state.Save();

        Assert.True(File.Exists(_statePath));

        AppState loaded = AppState.Load();

        Assert.Equal(["chrome", "outlook"], loaded.AutoAddApps);
        Assert.Equal(320, loaded.LeftPanelWidth);
        Assert.Equal(240, loaded.AppListHeight);
        Assert.True(loaded.EnableOsNotificationAttention);
        Assert.Single(loaded.Slots);
        Assert.Equal("notepad", loaded.Slots[0].ProcessName);
        Assert.Equal("notes.txt", loaded.Slots[0].Title);
        Assert.True(loaded.Slots[0].IsPinned);
        Assert.Equal(123L, loaded.Slots[0].Identity?.Handle);
        Assert.Equal(456U, loaded.Slots[0].Identity?.ProcessId);
        Assert.Equal(789L, loaded.Slots[0].Identity?.ProcessStartTimeUtcTicks);
        Assert.Equal(@"C:\Windows\notepad.exe", loaded.Slots[0].Identity?.ExecutablePath);
        Assert.Equal("Notepad", loaded.Slots[0].Identity?.WindowClassName);
        Assert.Equal("notepad.app", loaded.Slots[0].Identity?.AppUserModelId);
    }

    [Fact]
    public void Save_ReplacesStateWithoutLeavingTemporaryFiles()
    {
        new AppState { AutoAddApps = ["chrome"] }.Save();
        new AppState { AutoAddApps = ["outlook"] }.Save();

        Assert.Equal(["outlook"], AppState.Load().AutoAddApps);
        Assert.Equal([_statePath], Directory.GetFiles(_tempDirectory));
    }

    [Fact]
    public void Save_KeepsRulesWhenWindowGeometryIsUnavailable()
    {
        new AppState
        {
            Geometry = WindowGeometry.FromBounds(double.PositiveInfinity, double.PositiveInfinity,
                double.NegativeInfinity, double.NegativeInfinity, false),
            AutoAddApps = ["chrome"]
        }.Save();

        AppState loaded = AppState.Load();
        Assert.Null(loaded.Geometry);
        Assert.Equal(["chrome"], loaded.AutoAddApps);
    }

    [Fact]
    public void Load_LegacySlotWithoutIdentityRemainsReadable()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(_statePath, """{"Slots":[{"ProcessName":"chrome","Title":"old title"}]}""");

        AppState loaded = AppState.Load();

        SlotState slot = Assert.Single(loaded.Slots);
        Assert.Equal("chrome", slot.ProcessName);
        Assert.Equal("old title", slot.Title);
        Assert.Null(slot.Identity);
    }

    [Fact]
    public void Load_IgnoresOldWindowBoundsWithoutLosingIdentity()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(_statePath, """
            {"Slots":[{"ProcessName":"chrome","Title":"WhatsApp","Identity":{
                "WindowClassName":"Chrome_WidgetWin_1","Bounds":{"Left":0,"Top":0,"Right":800,"Bottom":600}
            }}]}
            """);

        SlotState slot = Assert.Single(AppState.Load().Slots);
        Assert.Equal("WhatsApp", slot.Title);
        Assert.Equal("Chrome_WidgetWin_1", slot.Identity?.WindowClassName);
    }

    [Fact]
    public void Load_DefaultsNotificationAttentionToDisabled()
    {
        AppState loaded = AppState.Load();

        Assert.False(loaded.EnableOsNotificationAttention);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppState.StatePathOverrideEnvironmentVariable, _originalOverridePath);
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
