namespace WindowThumbWall;

/// <summary>
/// Manages one DWM thumbnail registration bound to a <see cref="ThumbHost"/> cell.
/// The thumbnail is rendered by the DWM compositor on the top-level owner window
/// at the screen position that corresponds to the child HWND of the ThumbHost.
/// </summary>
internal sealed class ThumbnailSlot
{
    private readonly ThumbHost _host;
    private readonly IntPtr _ownerHwnd;
    private readonly DwmThumbnailRegistration _thumbnail;
    private bool _hasAppliedState;
    private bool _lastVisible;
    private NativeMethods.RECT _lastDestination;

    internal Guid SlotId { get; }
    internal ThumbnailBinding? Binding { get; private set; }
    internal IntPtr SourceHwnd => Binding?.Handle ?? IntPtr.Zero;
    internal string SourceTitle { get; private set; } = string.Empty;
    internal string SourceProcessName { get; private set; } = string.Empty;
    internal WindowIdentity? Identity { get; private set; }
    internal SlotState? Intent { get; private set; }
    internal SlotState? UnresolvedState => IsOccupied ? null : Intent;
    internal bool IsPinned => Intent?.RuleId != null;
    internal bool IsOccupied => SourceHwnd != IntPtr.Zero;
    internal bool IsUnresolved => !IsOccupied && UnresolvedState != null;
    internal bool HasRegistration => _thumbnail.Handle != IntPtr.Zero;

    internal ThumbnailSlot(ThumbHost host, IntPtr ownerHwnd, Guid? slotId = null)
    {
        _host = host;
        _ownerHwnd = ownerHwnd;
        SlotId = slotId ?? Guid.NewGuid();
        _thumbnail = new DwmThumbnailRegistration(AcquireThumbnail, static handle => NativeMethods.DwmUnregisterThumbnail(handle));
    }

    internal bool Assign(ThumbnailBinding binding, string title, string processName, WindowIdentity identity, SlotState intent)
    {
        if (intent.SlotId != SlotId || !_thumbnail.TryReplace(binding.Handle))
            return false;

        ResetAppliedState();
        Binding = binding;
        SourceTitle = title;
        SourceProcessName = processName;
        Identity = identity;
        intent.LastBindingIdentity = identity;
        Intent = intent;

        UpdateThumbnail();
        return true;
    }

    internal void SetUnresolved(SlotState state)
    {
        if (state.SlotId != SlotId)
            throw new ArgumentException("The saved intent belongs to a different slot.", nameof(state));
        Clear();
        Intent = state;
    }

    internal bool RefreshRegistration(ThumbnailBinding expected, TaskbarWindowOrder windows)
    {
        if (Binding != expected || !_thumbnail.TryRefresh(expected, windows, IsNativeSourceValid))
            return false;

        ResetAppliedState();
        UpdateThumbnail();
        return true;
    }

    internal void ReleaseRegistration()
    {
        _thumbnail.Release();
        ResetAppliedState();
    }

    internal void Clear()
    {
        ReleaseRegistration();
        ResetSourceInfo();
    }

    private void ResetAppliedState()
    {
        _hasAppliedState = false;
        _lastVisible = false;
        _lastDestination = default;
    }

    /// <summary>
    /// Recalculates the destination rectangle from the ThumbHost's native HWND
    /// position relative to the owner window's client area, then pushes it to DWM.
    /// Also auto-hides the thumbnail when the host HWND is not visible.
    /// </summary>
    internal void UpdateThumbnail()
    {
        if (_thumbnail.Handle == IntPtr.Zero || _host.Hwnd == IntPtr.Zero) return;

        // If the host HWND is hidden (e.g. cell collapsed), hide the DWM thumbnail.
        if (!NativeMethods.IsWindowVisible(_host.Hwnd))
        {
            if (_hasAppliedState && !_lastVisible)
                return;

            var hide = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_VISIBLE,
                fVisible = false
            };
            int hr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnail.Handle, ref hide);
            if (hr == 0)
            {
                _hasAppliedState = true;
                _lastVisible = false;
            }
            return;
        }

        NativeMethods.GetClientRect(_host.Hwnd, out var hostRect);
        if (hostRect.Right <= 0 || hostRect.Bottom <= 0) return;

        var hostPt = new NativeMethods.POINT();
        NativeMethods.ClientToScreen(_host.Hwnd, ref hostPt);

        var mainPt = new NativeMethods.POINT();
        NativeMethods.ClientToScreen(_ownerHwnd, ref mainPt);

        int left   = hostPt.X - mainPt.X;
        int top    = hostPt.Y - mainPt.Y;
        int right  = left + hostRect.Right;
        int bottom = top + hostRect.Bottom;
        var destination = new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };

        if (_hasAppliedState && _lastVisible && RectEquals(_lastDestination, destination))
            return;

        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE,
            rcDestination = destination,
            fVisible = true
        };
        int updateHr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnail.Handle, ref props);
        if (updateHr == 0)
        {
            _hasAppliedState = true;
            _lastVisible = true;
            _lastDestination = destination;
        }
    }

    /// <summary>
    /// Returns false (and auto-clears) if the source window no longer exists.
    /// </summary>
    internal bool CheckValid()
    {
        if (SourceHwnd == IntPtr.Zero) return true;
        if (!NativeMethods.IsWindow(SourceHwnd))
        {
            Clear();
            return false;
        }
        return true;
    }

    internal bool IsSourceWindowValid(TaskbarWindowOrder windows) =>
        Binding is ThumbnailBinding binding && binding.IsObservedCurrent(windows) && IsNativeSourceValid();

    private bool IsNativeSourceValid() =>
        NativeMethods.IsWindow(SourceHwnd) && Identity?.IsSameLiveProcessAndClass(SourceHwnd) == true;

    internal void UpdateSourceTitle(string title)
    {
        SourceTitle = title;
    }

    internal void UpdateIdentity(WindowIdentity identity)
    {
        Identity = identity;
    }

    private IntPtr AcquireThumbnail(IntPtr sourceHwnd)
    {
        if (_host.Hwnd == IntPtr.Zero || sourceHwnd == IntPtr.Zero)
            return IntPtr.Zero;

        int hr = NativeMethods.DwmRegisterThumbnail(_ownerHwnd, sourceHwnd, out IntPtr id);
        return hr >= 0 ? id : IntPtr.Zero;
    }

    private void ResetSourceInfo()
    {
        Binding = null;
        SourceTitle = string.Empty;
        SourceProcessName = string.Empty;
        Identity = null;
        Intent = null;
    }

    private static bool RectEquals(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left == b.Left &&
        a.Top == b.Top &&
        a.Right == b.Right &&
        a.Bottom == b.Bottom;
}
