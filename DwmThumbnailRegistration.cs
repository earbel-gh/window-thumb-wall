namespace WindowThumbWall;

internal readonly record struct ThumbnailBinding(IntPtr Handle, long Lifetime)
{
    internal bool IsObservedCurrent(TaskbarWindowOrder windows) => windows.GetLifetime(Handle) == Lifetime;
}

internal sealed class DwmThumbnailRegistration(Func<IntPtr, IntPtr> register, Action<IntPtr> unregister)
{
    internal IntPtr Handle { get; private set; }

    internal bool TryReplace(IntPtr source)
    {
        IntPtr replacement = register(source);
        if (replacement == IntPtr.Zero)
            return false;

        Release();
        Handle = replacement;
        return true;
    }

    internal bool TryRefresh(ThumbnailBinding binding, TaskbarWindowOrder windows, Func<bool> isNativeSourceValid) =>
        binding.IsObservedCurrent(windows) && isNativeSourceValid() && TryReplace(binding.Handle);

    internal void Release()
    {
        if (Handle == IntPtr.Zero)
            return;

        IntPtr previous = Handle;
        Handle = IntPtr.Zero;
        unregister(previous);
    }
}
