namespace WindowThumbWall;

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

    internal void Release()
    {
        if (Handle == IntPtr.Zero)
            return;

        IntPtr previous = Handle;
        Handle = IntPtr.Zero;
        unregister(previous);
    }
}
