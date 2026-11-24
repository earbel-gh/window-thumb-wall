namespace WindowThumbWall.Tests;

public sealed class WindowRestoreMatcherTests
{
    [Fact]
    public void Match_UsesValidatedLiveHandleWhenUnreadCountChanges()
    {
        SlotState saved = Saved("(2) WhatsApp - Google Chrome", Identity(11, 42, 123));
        WindowInfo candidate = Window(11, "(3) WhatsApp - Google Chrome", Identity(11, 42, 123));
        WindowInfo sibling = Window(22, saved.Title, Identity(22, 42, 123));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [sibling, candidate]);

        Assert.Same(candidate, Assert.Single(matches).Value);
    }

    [Theory]
    [InlineData("New Tab - Google Chrome")]
    [InlineData("Team calendar - Google Chrome")]
    [InlineData("")]
    public void Match_DoesNotUseLoneSiblingWithWeakTitle(string title)
    {
        SlotState saved = Saved("WhatsApp - Google Chrome", Identity(11, 42, 123));
        WindowInfo candidate = Window(22, title, Identity(22, 84, 456));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [candidate]);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_LeavesAmbiguousSameAppWindowsUnresolved()
    {
        SlotState saved = Saved("(2) WhatsApp - Google Chrome", Identity(11, 42, 123));
        WindowInfo[] candidates =
        [
            Window(22, saved.Title, Identity(22, 84, 456)),
            Window(33, "(3) WhatsApp - Google Chrome", Identity(33, 84, 456))
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_DoesNotSubstituteSiblingFromOriginalProcessEvenWithSameTitle()
    {
        SlotState saved = Saved("same title", Identity(11, 42, 123));
        WindowInfo sibling = Window(22, saved.Title, Identity(22, 42, 123));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [sibling]);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_AfterRebootIgnoresUnreadCountAndUsesNoPlacement()
    {
        SlotState saved = Saved("(2) WhatsApp - Google Chrome", Identity(11, 42, 123));
        WindowInfo[] candidates =
        [
            Window(22, "(37)  WHATSAPP - Google Chrome", Identity(22, 84, 456)),
            Window(33, "Team calendar - Google Chrome", Identity(33, 84, 456))
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Equal((IntPtr)22, Assert.Single(matches).Value.Handle);
    }

    [Fact]
    public void Match_DistinguishesWaitingFromAmbiguityOnLaterPasses()
    {
        SlotState saved = Saved("(2) WhatsApp - Google Chrome", Identity(11, 42, 123));
        WindowInfo first = Window(22, "(37) WhatsApp - Google Chrome", Identity(22, 84, 456));
        WindowInfo second = Window(33, "WhatsApp - Google Chrome", Identity(33, 84, 456));
        var needsChoice = new HashSet<int>();

        Assert.Empty(WindowRestoreMatcher.Match([saved], [], needsChoice));
        Assert.Empty(needsChoice);
        Assert.Empty(WindowRestoreMatcher.Match([saved], [first, second], needsChoice));
        Assert.Equal(0, Assert.Single(needsChoice));

        needsChoice.Clear();
        Assert.Same(first, Assert.Single(WindowRestoreMatcher.Match([saved], [first], needsChoice)).Value);
        Assert.Empty(needsChoice);
    }

    [Theory]
    [InlineData("Project planning dashboards", "News headlines", true)]
    [InlineData("Project planning dashboards", "Project planning dashboard", false)]
    public void Match_RequiresClearLeadForApproximateTitle(string bestTitle, string otherTitle, bool shouldRestore)
    {
        SlotState saved = Saved("Project planning dashboard", Identity(11, 42, 123));
        WindowInfo best = Window(22, bestTitle, Identity(22, 84, 456));
        WindowInfo other = Window(33, otherTitle, Identity(33, 84, 456));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], [other, best]);

        if (shouldRestore)
            Assert.Same(best, Assert.Single(matches).Value);
        else
            Assert.Empty(matches);
    }

    [Theory]
    [InlineData("Quarterly financial report Q4 2026", "Quarterly financial report Q4 2027")]
    [InlineData("New Tab - Google Chrome", "New Tab - Google Chrome")]
    public void Match_RejectsChangedMeaningfulNumbersAndExactGenericTitles(string savedTitle, string currentTitle)
    {
        SlotState saved = Saved(savedTitle, Identity(11, 42, 123));
        WindowInfo candidate = Window(22, currentTitle, Identity(22, 84, 456));

        Assert.Empty(WindowRestoreMatcher.Match([saved], [candidate]));
    }

    [Fact]
    public void Match_PrioritizesLiveMatchOverFuzzyClaimOnSameWindow()
    {
        SlotState fuzzy = Saved("Project planning dashboard", Identity(11, 42, 123));
        SlotState live = Saved("Old tab title", Identity(22, 84, 456));
        WindowInfo candidate = Window(22, fuzzy.Title, Identity(22, 84, 456));

        var match = Assert.Single(WindowRestoreMatcher.Match([fuzzy, live], [candidate]));

        Assert.Equal(1, match.Key);
        Assert.Same(candidate, match.Value);
    }

    [Fact]
    public void Match_UsesWindowClassToDistinguishMainAndSecondaryWindows()
    {
        string outlookPath = @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE";
        SlotState saved = Saved(
            "Inbox - Outlook",
            Identity(null, null, null, appUserModelId: "outlook.app", windowClassName: "OutlookMainWindow", executablePath: outlookPath),
            "outlook");
        WindowInfo[] candidates =
        [
            Window(22, "Inbox - Outlook", Identity(22, 84, 456, appUserModelId: "outlook.app", windowClassName: "OutlookMainWindow", executablePath: outlookPath), "outlook"),
            Window(33, "Inbox - Outlook", Identity(33, 84, 456, appUserModelId: "outlook.app", windowClassName: "OutlookMessageWindow", executablePath: outlookPath), "outlook")
        ];

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match([saved], candidates);

        Assert.Equal((IntPtr)22, Assert.Single(matches).Value.Handle);
    }

    [Fact]
    public void Match_DoesNotTrustAReusedHandleWhenProcessInstanceDiffers()
    {
        SlotState saved = Saved("WhatsApp", Identity(22, 84, 456));
        WindowInfo[] candidates =
        [
            Window(22, "New Tab", Identity(22, 99, 123)),
            Window(33, "Team calendar", Identity(33, 84, 456))
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
            Saved("first", Identity(22, 84, 456)),
            Saved("second", Identity(22, 84, 456))
        ];
        WindowInfo candidate = Window(22, "changed", Identity(22, 84, 456));

        IReadOnlyDictionary<int, WindowInfo> matches = WindowRestoreMatcher.Match(savedSlots, [candidate]);

        Assert.Empty(matches);
    }

    [Fact]
    public void Match_RejectsAppUserModelIdMismatch()
    {
        SlotState saved = Saved("WhatsApp", Identity(null, null, null, appUserModelId: "saved.app"));
        WindowInfo candidate = Window(22, "WhatsApp", Identity(22, 84, 456, appUserModelId: "different.app"));

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
        string executablePath = @"C:\Program Files\Chrome\chrome.exe") => new()
    {
        Handle = handle,
        ProcessId = processId,
        ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
        ExecutablePath = executablePath,
        WindowClassName = windowClassName,
        AppUserModelId = appUserModelId
    };
}
