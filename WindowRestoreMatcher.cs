using System.Text.RegularExpressions;

namespace WindowThumbWall;

internal static class WindowRestoreMatcher
{
    private const double MinimumTitleSimilarity = 0.85;
    private const double MinimumTitleLead = 0.10;
    private const int MaximumFuzzyTitleLength = 512;

    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Regex UnreadCountPrefix = new(@"^\(\p{Nd}+\)\s+");
    private static readonly Regex Numbers = new(@"\p{Nd}+");

    internal static IReadOnlyDictionary<int, WindowInfo> Match(
        IReadOnlyList<SlotState> savedSlots,
        IReadOnlyList<WindowInfo> windows,
        ISet<int>? needsChoice = null,
        IReadOnlySet<IntPtr>? unavailableHandles = null)
    {
        var liveMatches = new Dictionary<int, WindowInfo>();
        var liveMatchHandles = new HashSet<IntPtr>();
        var unmatchedSlots = new List<int>();

        for (int slotIndex = 0; slotIndex < savedSlots.Count; slotIndex++)
        {
            SlotState saved = savedSlots[slotIndex];
            WindowInfo[] candidates = windows
                .Where(window => IsValidatedHandleMatch(saved, window))
                .ToArray();

            foreach (WindowInfo candidate in candidates)
                liveMatchHandles.Add(candidate.Handle);
            if (candidates.Length == 1)
            {
                if (unavailableHandles?.Contains(candidates[0].Handle) == true)
                    needsChoice?.Add(slotIndex);
                else
                    liveMatches.Add(slotIndex, candidates[0]);
            }
            else if (candidates.Length == 0)
                unmatchedSlots.Add(slotIndex);
            else
                needsChoice?.Add(slotIndex);
        }

        var titleMatches = new Dictionary<int, WindowInfo>();
        foreach (int slotIndex in unmatchedSlots)
        {
            WindowInfo? match = FindBestTitleMatch(savedSlots[slotIndex], windows, out bool ambiguous);
            // Rank before excluding claimed windows; don't silently promote a runner-up.
            if (match != null &&
                !liveMatchHandles.Contains(match.Handle) &&
                unavailableHandles?.Contains(match.Handle) != true)
                titleMatches.Add(slotIndex, match);
            else if (ambiguous || match != null)
                needsChoice?.Add(slotIndex);
        }

        var result = new Dictionary<int, WindowInfo>();
        foreach (var group in liveMatches.Concat(titleMatches).GroupBy(pair => pair.Value.Handle))
        {
            if (group.Count() == 1)
            {
                var match = group.Single();
                result.Add(match.Key, match.Value);
            }
            else
            {
                foreach (var match in group)
                    needsChoice?.Add(match.Key);
            }
        }
        return result;
    }

    private static WindowInfo? FindBestTitleMatch(SlotState saved, IReadOnlyList<WindowInfo> windows, out bool ambiguous)
    {
        ambiguous = false;
        if (saved.Identity is not { } identity || !HasStrongEvidence(identity))
            return null;

        WindowIdentity lastBindingIdentity = saved.LastBindingIdentity ?? identity;

        string savedTitle = NormalizeTitle(saved.Title, saved.ProcessName);
        if (!IsMeaningfulTitle(savedTitle))
            return null;

        var matches = windows
            .Where(window => window.Handle != IntPtr.Zero && MatchesStrongEvidence(saved, window))
            .Select(window => (Window: window, Title: NormalizeTitle(window.Title, window.ProcessName)))
            .Where(candidate => IsMeaningfulTitle(candidate.Title))
            .Select(candidate => (candidate.Window, Score: TitleSimilarity(savedTitle, candidate.Title)))
            .OrderByDescending(candidate => candidate.Score)
            .Take(2)
            .ToArray();

        if (matches.Length == 0 || matches[0].Score < MinimumTitleSimilarity)
            return null;

        WindowInfo best = matches[0].Window;
        // A different handle in the original process may just be a surviving sibling.
        ambiguous = (matches.Length == 2 && matches[0].Score < matches[1].Score + MinimumTitleLead) ||
            (best.Identity is { } candidate && IsSameProcessInstance(lastBindingIdentity, candidate));
        return ambiguous ? null : best;
    }

    private static string NormalizeTitle(string? title, string? processName)
    {
        string normalized = Whitespace.Replace(title ?? string.Empty, " ").Trim().ToLowerInvariant();
        string? browserName = processName?.ToLowerInvariant() switch
        {
            "chrome" => "google chrome",
            "msedge" => "microsoft edge",
            "firefox" => "mozilla firefox",
            _ => null
        };

        if (browserName != null)
        {
            string suffix = " - " + browserName;
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                normalized = normalized[..^suffix.Length].TrimEnd();
            Match unreadPrefix = UnreadCountPrefix.Match(normalized);
            if (unreadPrefix.Success &&
                string.Equals(normalized[unreadPrefix.Length..], "whatsapp", StringComparison.Ordinal))
            {
                normalized = normalized[unreadPrefix.Length..];
            }
        }

        return normalized;
    }

    private static bool IsMeaningfulTitle(string title) =>
        !IsGenericTitle(title) &&
        ((title.Length >= 4 && title.Any(char.IsLetter)) || CountJapaneseLetters(title) >= 2);

    private static bool IsFuzzyTitle(string title) =>
        !IsGenericTitle(title) && title.Length >= 4 && title.Any(char.IsLetter);

    private static bool IsGenericTitle(string title) =>
        title is "new tab" or "new window" or "about:blank" or "untitled" or
            "google chrome" or "microsoft edge" or "mozilla firefox" or "outlook" or "microsoft outlook" or
            "新しいタブ" or "新規タブ" or "無題" or "空白" or "空白ページ" or
            "ホーム" or "スタートページ" or "新しいウィンドウ" or "タイトルなし";

    private static int CountJapaneseLetters(string title) => title.Count(character =>
        (int)character is >= 0x3040 and <= 0x309F or
        >= 0x30A0 and <= 0x30FF or
        >= 0x31F0 and <= 0x31FF or
        >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF);

    private static double TitleSimilarity(string saved, string candidate)
    {
        if (saved == candidate)
            return IsMeaningfulTitle(saved) ? 1 : 0;

        if (!IsFuzzyTitle(saved) || !IsFuzzyTitle(candidate))
            return 0;

        int length = Math.Max(saved.Length, candidate.Length);
        if (length > MaximumFuzzyTitleLength ||
            !Numbers.Matches(saved).Select(match => match.Value)
                .SequenceEqual(Numbers.Matches(candidate).Select(match => match.Value)))
            return 0;

        // Levenshtein edit distance using one row.
        var row = new int[candidate.Length + 1];
        for (int j = 0; j < row.Length; j++)
            row[j] = j;

        for (int i = 1; i <= saved.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j < row.Length; j++)
            {
                int above = row[j];
                int substitution = diagonal + (saved[i - 1] == candidate[j - 1] ? 0 : 1);
                row[j] = Math.Min(substitution, 1 + Math.Min(above, row[j - 1]));
                diagonal = above;
            }
        }

        return 1 - (double)row[^1] / length;
    }

    private static bool IsValidatedHandleMatch(SlotState saved, WindowInfo window)
    {

        if ((saved.LastBindingIdentity ?? saved.Identity) is not
                {
                    Handle: long savedHandle
                } savedIdentity ||
            window.Identity is not
                {
                    Handle: long currentHandle
                } currentIdentity)
        {
            return false;
        }

        if (savedHandle == 0 || savedHandle != currentHandle ||
            savedHandle != window.Handle.ToInt64() ||
            !IsSameProcessInstance(savedIdentity, currentIdentity) ||
            !MatchesStrongEvidence(saved, window) ||
            !MatchesStoredStrongFields(savedIdentity, currentIdentity))
        {
            return false;
        }

        // A saved HWND may have been reused by the same process while unobserved.
        string savedTitle = NormalizeTitle(saved.Title, saved.ProcessName);
        return IsMeaningfulTitle(savedTitle) &&
            string.Equals(savedTitle, NormalizeTitle(window.Title, window.ProcessName), StringComparison.Ordinal);
    }

    internal static bool IsSameProcessInstance(WindowIdentity saved, WindowIdentity current) =>
        saved.ProcessId is > 0 &&
        saved.ProcessId == current.ProcessId &&
        saved.ProcessStartTimeUtcTicks is > 0 &&
        saved.ProcessStartTimeUtcTicks == current.ProcessStartTimeUtcTicks;

    private static bool MatchesStrongEvidence(SlotState saved, WindowInfo window)
    {
        if (!string.Equals(saved.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase) ||
            saved.Identity is not WindowIdentity savedIdentity ||
            !HasStrongEvidence(savedIdentity) ||
            window.Identity is not WindowIdentity windowIdentity ||
            !string.Equals(savedIdentity.ExecutablePath, windowIdentity.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(savedIdentity.WindowClassName, windowIdentity.WindowClassName, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(savedIdentity.AppUserModelId) ||
            string.Equals(savedIdentity.AppUserModelId, windowIdentity.AppUserModelId, StringComparison.Ordinal);
    }

    private static bool MatchesStoredStrongFields(WindowIdentity saved, WindowIdentity? current)
    {
        if (current == null)
            return false;

        return (string.IsNullOrWhiteSpace(saved.ExecutablePath) ||
                string.Equals(saved.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(saved.WindowClassName) ||
                string.Equals(saved.WindowClassName, current.WindowClassName, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(saved.AppUserModelId) ||
                string.Equals(saved.AppUserModelId, current.AppUserModelId, StringComparison.Ordinal));
    }

    private static bool HasStrongEvidence(WindowIdentity? identity) =>
        identity != null &&
        !string.IsNullOrWhiteSpace(identity.ExecutablePath) &&
        !string.IsNullOrWhiteSpace(identity.WindowClassName);
}
