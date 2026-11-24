using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;

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

    [Fact]
    public void RestoreAndSave_PreservesLegacySlotOrderAndAppRulesWithoutCreatingPins()
    {
        WriteState("""
            {
              "AutoAddApps": ["chrome", "outlook"],
              "Slots": [
                { "ProcessName": "chrome", "Title": "Saved first" },
                { "ProcessName": "notepad", "Title": "Saved second" },
                { "ProcessName": "outlook", "Title": "Saved third" }
              ]
            }
            """);

        for (int i = 0; i < 2; i++)
        {
            RestoreAndClose(Visibility.Collapsed);
            Assert.Contains("\"IsPinned\"", File.ReadAllText(_statePath));
            AppState saved = AppState.Load();
            Assert.Equal(["chrome", "outlook"], saved.AutoAddApps);
            Assert.Equal(["Saved first", "Saved second", "Saved third"], saved.Slots.Select(slot => slot.Title));
            Assert.All(saved.Slots, slot => Assert.False(slot.IsPinned));
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Slots\":[null]}")]
    public void RestoreAndSave_LeavesUnreadableSettingsUntouched(string original)
    {
        WriteState(original);

        RestoreAndClose(Visibility.Visible);

        Assert.Equal(original, File.ReadAllText(_statePath));
    }

    [Fact]
    public void RestoreAndSave_DoesNotOverwriteSettingsAfterAnInitialReadLockIsReleased()
    {
        const string original = """{"AutoAddApps":["chrome"]}""";
        WriteState(original);
        using var stateLock = File.Open(_statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        RestoreAndClose(Visibility.Visible, stateLock.Dispose);

        Assert.Equal(original, File.ReadAllText(_statePath));
        RestoreAndClose(Visibility.Collapsed);
        Assert.Equal(["chrome"], AppState.Load().AutoAddApps);
    }

    private void WriteState(string contents)
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(_statePath, contents);
    }

    private static void RestoreAndClose(Visibility noticeVisibility, Action? beforeClose = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                Invoke(window, "RestoreAutoAddApps");
                Invoke(window, "RestoreSlots");
                Assert.Equal(noticeVisibility, ((UIElement)window.FindName("StateNotice")).Visibility);
                beforeClose?.Invoke();
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(12)), "The WPF restore and close operation did not finish.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Invoke(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppState.StatePathOverrideEnvironmentVariable, _originalOverridePath);
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
