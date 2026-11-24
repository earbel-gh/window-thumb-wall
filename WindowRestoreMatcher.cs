namespace WindowThumbWall;

internal static class WindowRestoreMatcher
{
    internal static IReadOnlyDictionary<int, WindowInfo> Match(
        IReadOnlyList<SlotState> savedSlots,
        IReadOnlyList<WindowInfo> windows)
    {
        var proposedMatches = new Dictionary<int, WindowInfo>();

        for (int slotIndex = 0; slotIndex < savedSlots.Count; slotIndex++)
        {
            SlotState saved = savedSlots[slotIndex];
            WindowInfo[] liveHandleMatches = windows
                .Where(window => IsValidatedHandleMatch(saved, window))
                .ToArray();

            WindowInfo? match = liveHandleMatches.Length == 1
                ? liveHandleMatches[0]
                : liveHandleMatches.Length > 1
                    ? null
                    : FindUniqueDurableMatch(saved, windows);

            if (match != null)
                proposedMatches[slotIndex] = match;
        }

        HashSet<IntPtr> multiplyAssignedHandles = proposedMatches.Values
            .GroupBy(window => window.Handle)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        return proposedMatches
            .Where(pair => !multiplyAssignedHandles.Contains(pair.Value.Handle))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static WindowInfo? FindUniqueDurableMatch(SlotState saved, IReadOnlyList<WindowInfo> windows)
    {
        if (!HasStrongEvidence(saved.Identity))
            return null;

        WindowInfo[] compatible = windows
            .Where(window => MatchesStrongEvidence(saved, window))
            .ToArray();

        if (compatible.Length == 1)
            return compatible[0];

        if (compatible.Length < 2)
            return null;

        if (!string.IsNullOrWhiteSpace(saved.Title))
        {
            WindowInfo[] titleMatches = compatible
                .Where(window => string.Equals(window.Title, saved.Title, StringComparison.Ordinal))
                .ToArray();
            if (titleMatches.Length == 1)
                return titleMatches[0];
        }

        if (saved.Identity?.Bounds is WindowBounds savedBounds)
        {
            WindowInfo[] boundsMatches = compatible
                .Where(window => window.Identity?.Bounds == savedBounds)
                .ToArray();
            if (boundsMatches.Length == 1)
                return boundsMatches[0];
        }

        return null;
    }

    private static bool IsValidatedHandleMatch(SlotState saved, WindowInfo window)
    {
        if (saved.Identity is not
                {
                    Handle: long savedHandle,
                    ProcessId: uint savedProcessId,
                    ProcessStartTimeUtcTicks: long savedStartTime
                } savedIdentity ||
            window.Identity is not
                {
                    Handle: long currentHandle,
                    ProcessId: uint currentProcessId,
                    ProcessStartTimeUtcTicks: long currentStartTime
                } currentIdentity)
        {
            return false;
        }

        return savedHandle == currentHandle &&
            savedHandle == window.Handle.ToInt64() &&
            savedProcessId == currentProcessId &&
            savedStartTime == currentStartTime &&
            MatchesStoredStrongFields(savedIdentity, currentIdentity);
    }

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
