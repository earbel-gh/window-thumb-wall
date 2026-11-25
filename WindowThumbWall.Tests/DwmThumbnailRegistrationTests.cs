namespace WindowThumbWall.Tests;

public sealed class DwmThumbnailRegistrationTests
{
    [Fact]
    public void FailedReplacementKeepsTheExistingRegistration()
    {
        var released = new List<IntPtr>();
        var registration = new DwmThumbnailRegistration(source => source == (IntPtr)1 ? (IntPtr)101 : IntPtr.Zero, released.Add);

        Assert.True(registration.TryReplace((IntPtr)1));
        Assert.False(registration.TryReplace((IntPtr)2));

        Assert.Equal((IntPtr)101, registration.Handle);
        Assert.Empty(released);
    }

    [Fact]
    public void DeferredRefreshNeverAcquiresAReusedHandleFromAnOldLifetime()
    {
        IntPtr source = (IntPtr)42;
        var windows = new TaskbarWindowOrder();
        windows.ObserveCreated(source);
        var pending = new ThumbnailBinding(source, windows.GetLifetime(source)!.Value);
        int acquisitions = 0;
        var released = new List<IntPtr>();
        var registration = new DwmThumbnailRegistration(_ => (IntPtr)(100 + ++acquisitions), released.Add);
        Assert.True(registration.TryReplace(source));

        windows.ObserveDestroyed(source);
        windows.ObserveCreated(source);
        Assert.False(registration.TryRefresh(pending, windows, () => true));
        Assert.Equal(1, acquisitions);
        Assert.Equal((IntPtr)101, registration.Handle);
        Assert.Empty(released);

        var current = new ThumbnailBinding(source, windows.GetLifetime(source)!.Value);
        Assert.False(registration.TryRefresh(current, windows, () => false));
        Assert.True(registration.TryRefresh(current, windows, () => true));
        Assert.Equal(2, acquisitions);
        Assert.Equal([(IntPtr)101], released);
    }

    [Fact]
    public void SuccessfulReplacementAcquiresBeforeReleasing()
    {
        var calls = new List<(string Operation, IntPtr Value)>();
        var registration = new DwmThumbnailRegistration(source =>
        {
            calls.Add(("acquire", source));
            return source + 100;
        }, handle => calls.Add(("release", handle)));

        Assert.True(registration.TryReplace((IntPtr)1));
        Assert.True(registration.TryReplace((IntPtr)2));
        registration.Release();
        registration.Release();

        Assert.Equal([("acquire", (IntPtr)1), ("acquire", (IntPtr)2), ("release", (IntPtr)101), ("release", (IntPtr)102)], calls);
        Assert.Equal(IntPtr.Zero, registration.Handle);
    }
}
