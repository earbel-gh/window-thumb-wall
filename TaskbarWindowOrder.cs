namespace WindowThumbWall;

internal sealed class TaskbarWindowOrder
{
    private readonly List<IntPtr> _handles = [];
    private readonly Dictionary<IntPtr, long> _windowLifetimes = [];
    private long _nextLifetime;

    internal void ObserveWindowsInZOrder(IEnumerable<IntPtr> handlesInZOrder)
    {
        // The taskbar keeps normal windows in creation order. Windows that already
        // existed when this app started have no public creation-order API, so use
        // bottom-to-top Z order as the stable oldest-first fallback for that set.
        foreach (IntPtr handle in handlesInZOrder.Reverse())
            ObserveCreated(handle);
    }

    internal void ObserveCreated(IntPtr handle)
    {
        if (handle == IntPtr.Zero || _windowLifetimes.ContainsKey(handle))
            return;

        _windowLifetimes.Add(handle, ++_nextLifetime);
        _handles.Add(handle);
    }

    internal void ObserveDestroyed(IntPtr handle)
    {
        if (!_windowLifetimes.Remove(handle))
            return;

        _handles.Remove(handle);
    }

    internal long? GetLifetime(IntPtr handle) =>
        _windowLifetimes.TryGetValue(handle, out long lifetime) ? lifetime : null;

    internal IReadOnlyList<T> Order<T>(IEnumerable<T> windowsInZOrder, Func<T, IntPtr> handleSelector)
    {
        List<T> windows = windowsInZOrder
            .Where(window => handleSelector(window) != IntPtr.Zero)
            .ToList();
        ObserveWindowsInZOrder(windows.Select(handleSelector));

        var orderByHandle = _handles
            .Select((handle, index) => new { handle, index })
            .ToDictionary(item => item.handle, item => item.index);

        return windows
            .OrderBy(window => orderByHandle[handleSelector(window)])
            .ToList();
    }
}
