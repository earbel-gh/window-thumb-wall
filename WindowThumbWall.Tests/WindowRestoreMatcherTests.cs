namespace WindowThumbWall.Tests;

public sealed class WindowRestoreMatcherTests
{
    [Fact]
    public void Match_UsesValidatedLiveHandleWhenTitleChanges()
    {
        SlotState saved = Saved("old title", Identity(11, 42, 123));
        WindowInfo candidate = Window(11, "new title", Identity(11, 42, 123));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [candidate]);

        Assert.Same(candidate, Assert.Single(matches).Value);
    }

    [Fact]
    public void Match_UsesUniqueStrongEvidenceWhenHandleAndTitleChange()
    {
        SlotState saved = Saved("old tab", Identity(null, null, null));
        WindowInfo candidate = Window(22, "new tab", Identity(22, 84, 456));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [candidate]);

        Assert.Same(candidate, Assert.Single(matches).Value);
    }

    [Fact]
    public void Match_LeavesAmbiguousSameAppWindowsUnresolved()
    {
        SlotState saved = Saved("old tab", Identity(null, null, null));
        WindowInfo[] candidates =
        [
            Window(22, "changed tab one", Identity(22, 84, 456)),
            Window(33, "changed tab two", Identity(33, 84, 456))
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_UsesTitleOnlyToBreakATieBetweenStrongMatches()
    {
        SlotState saved = Saved("same title", Identity(null, null, null));
        WindowInfo[] candidates =
        [
            Window(22, "same title", Identity(22, 84, 456)),
            Window(33, "different title", Identity(33, 84, 456))
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Equal((IntPtr)22, Assert.Single(matches).Value.Handle);
    }

    [Fact]
    public void Match_UsesBoundsOnlyToBreakATieBetweenStrongMatches()
    {
        WindowBounds savedBounds = new(0, 0, 800, 600);
        SlotState saved = Saved("old title", Identity(null, null, null, bounds: savedBounds));
        WindowInfo[] candidates =
        [
            Window(22, "changed title one", Identity(22, 84, 456, bounds: savedBounds)),
            Window(33, "changed title two", Identity(33, 84, 456, bounds: new WindowBounds(900, 0, 1700, 600)))
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Equal((IntPtr)22, Assert.Single(matches).Value.Handle);
    }

    [Fact]
    public void Match_UsesWindowClassToDistinguishMainAndSecondaryWindows()
    {
        string outlookPath = @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE";
        SlotState saved = Saved(
            "old main title",
            Identity(null, null, null, appUserModelId: "outlook.app", windowClassName: "OutlookMainWindow", executablePath: outlookPath),
            "outlook");
        WindowInfo[] candidates =
        [
            Window(22, "Inbox", Identity(22, 84, 456, appUserModelId: "outlook.app", windowClassName: "OutlookMainWindow", executablePath: outlookPath), "outlook"),
            Window(33, "Popped-out message", Identity(33, 84, 456, appUserModelId: "outlook.app", windowClassName: "OutlookMessageWindow", executablePath: outlookPath), "outlook")
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Equal((IntPtr)22, Assert.Single(matches).Value.Handle);
    }

    [Fact]
    public void Match_DoesNotTrustAReusedHandleWhenProcessInstanceDiffers()
    {
        SlotState saved = Saved("old title", Identity(22, 84, 456));
        WindowInfo[] candidates =
        [
            Window(22, "new sibling one", Identity(22, 99, 123)),
            Window(33, "new sibling two", Identity(33, 84, 456))
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_DoesNotRestoreLegacyStateFromProcessAndTitleAlone()
    {
        SlotState saved = new() { ProcessName = "chrome", Title = "same title" };
        WindowInfo candidate = Window(22, "same title", Identity(22, 84, 456));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [candidate]);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_LeavesSlotsUnresolvedWhenTheyCompeteForTheSameWindow()
    {
        SlotState[] savedSlots =
        [
            Saved("first", Identity(null, null, null)),
            Saved("second", Identity(null, null, null))
        ];
        WindowInfo candidate = Window(22, "changed", Identity(22, 84, 456));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match(savedSlots, [candidate]);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_RejectsAppUserModelIdMismatch()
    {
        SlotState saved = Saved("title", Identity(null, null, null, appUserModelId: "saved.app"));
        WindowInfo candidate = Window(22, "title", Identity(22, 84, 456, appUserModelId: "different.app"));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [candidate]);

        Assert.Empty(matches);
    }

    private static SlotState Saved(string title, WindowIdentity identity, string processName = "chrome") => new()
    {
        ProcessName = processName,
        Title = title,
        Identity = identity
    };

    private static WindowInfo Window(long handle, string title, WindowIdentity identity, string processName = "chrome") => new()
    {
        Handle = (IntPtr)handle,
        ProcessName = processName,
        Title = title,
        Identity = identity
    };

    private static WindowIdentity Identity(
        long? handle,
        uint? processId,
        long? processStartTimeUtcTicks,
        string appUserModelId = "chrome.app",
        string windowClassName = "Chrome_WidgetWin_1",
        WindowBounds? bounds = null,
        string executablePath = @"C:\Program Files\Chrome\chrome.exe") => new()
    {
        Handle = handle,
        ProcessId = processId,
        ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
        ExecutablePath = executablePath,
        WindowClassName = windowClassName,
        AppUserModelId = appUserModelId,
        Bounds = bounds
    };
}
