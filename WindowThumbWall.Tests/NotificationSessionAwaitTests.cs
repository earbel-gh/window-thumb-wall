namespace WindowThumbWall.Tests;

public sealed class NotificationSessionAwaitTests
{
    private sealed class FakeSession
    {
        internal bool IsSubscribed { get; private set; }
        internal int SubscribeCount { get; private set; }
        internal int UnsubscribeCount { get; private set; }
        internal int AppliedResultCount { get; set; }

        internal void Subscribe()
        {
            IsSubscribed = true;
            SubscribeCount++;
        }

        internal void Unsubscribe()
        {
            if (!IsSubscribed)
                return;

            IsSubscribed = false;
            UnsubscribeCount++;
        }
    }

    [Fact]
    public async Task PendingInitializationCompletedAfterDisable_DoesNotSubscribe()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        bool enabled = true;
        FakeSession session = lifecycle.Ensure(static () => new FakeSession());
        TaskCompletionSource<bool> pendingInitialization = NewPending<bool>();

        Task<bool> initialization = lifecycle.AwaitAndApplyIfCurrentAsync(
            session,
            pendingInitialization.Task,
            current => lifecycle.IsCurrent(current, _ => enabled),
            _ => lifecycle.SubscribeIfCurrent(
                session,
                current => lifecycle.IsCurrent(current, _ => enabled),
                session.Subscribe,
                session.Unsubscribe));

        enabled = false;
        lifecycle.Invalidate(static oldSession => oldSession.Unsubscribe());
        pendingInitialization.SetResult(true);

        Assert.False(await initialization);
        Assert.Equal(0, session.SubscribeCount);
        Assert.False(session.IsSubscribed);
    }

    [Fact]
    public async Task PendingSyncCompletedAfterClose_DoesNotApplyAndUnsubscribes()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        bool enabled = true;
        FakeSession session = lifecycle.Ensure(static () => new FakeSession());
        Assert.True(lifecycle.SubscribeIfCurrent(
            session,
            current => lifecycle.IsCurrent(current, _ => enabled),
            session.Subscribe,
            session.Unsubscribe));
        TaskCompletionSource<int> pendingSync = NewPending<int>();

        Task<bool> sync = lifecycle.AwaitAndApplyIfCurrentAsync(
            session,
            pendingSync.Task,
            current => lifecycle.IsCurrent(current, _ => enabled),
            result => session.AppliedResultCount += result);

        enabled = false;
        lifecycle.Invalidate(static oldSession => oldSession.Unsubscribe());
        pendingSync.SetResult(3);

        Assert.False(await sync);
        Assert.Equal(0, session.AppliedResultCount);
        Assert.Equal(1, session.SubscribeCount);
        Assert.Equal(1, session.UnsubscribeCount);
        Assert.False(session.IsSubscribed);
    }

    [Fact]
    public async Task DisableThenEnable_ObsoleteInitializationCannotSubscribeOverNewSession()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        bool enabled = true;
        FakeSession oldSession = lifecycle.Ensure(static () => new FakeSession());
        TaskCompletionSource<bool> oldPending = NewPending<bool>();
        Task<bool> oldInitialization = lifecycle.AwaitAndApplyIfCurrentAsync(
            oldSession,
            oldPending.Task,
            current => lifecycle.IsCurrent(current, _ => enabled),
            _ => lifecycle.SubscribeIfCurrent(
                oldSession,
                current => lifecycle.IsCurrent(current, _ => enabled),
                oldSession.Subscribe,
                oldSession.Unsubscribe));

        enabled = false;
        lifecycle.Invalidate(static session => session.Unsubscribe());
        enabled = true;
        FakeSession newSession = lifecycle.Ensure(static () => new FakeSession());
        Assert.NotSame(oldSession, newSession);
        Assert.True(lifecycle.SubscribeIfCurrent(
            newSession,
            current => lifecycle.IsCurrent(current, _ => enabled),
            newSession.Subscribe,
            newSession.Unsubscribe));

        oldPending.SetResult(true);

        Assert.False(await oldInitialization);
        Assert.Equal(0, oldSession.SubscribeCount);
        Assert.False(oldSession.IsSubscribed);
        Assert.Equal(1, newSession.SubscribeCount);
        Assert.Equal(0, newSession.UnsubscribeCount);
        Assert.True(newSession.IsSubscribed);
    }

    [Fact]
    public async Task ObsoleteInitializationFailure_IsIgnoredButCurrentFailurePropagates()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        bool enabled = true;
        FakeSession obsoleteSession = lifecycle.Ensure(static () => new FakeSession());
        TaskCompletionSource<string> obsoletePending = NewPending<string>();
        Task<bool> obsoleteOperation = lifecycle.AwaitAndApplyIfCurrentAsync(
            obsoleteSession,
            obsoletePending.Task,
            current => lifecycle.IsCurrent(current, _ => enabled),
            _ => throw new InvalidOperationException("An obsolete result must not be applied."));

        enabled = false;
        lifecycle.Invalidate(static session => session.Unsubscribe());
        obsoletePending.SetException(new InvalidOperationException("obsolete failure"));

        Assert.False(await obsoleteOperation);

        enabled = true;
        FakeSession currentSession = lifecycle.Ensure(static () => new FakeSession());
        Task<bool> currentOperation = lifecycle.AwaitAndApplyIfCurrentAsync(
            currentSession,
            Task.FromException<string>(new InvalidOperationException("current failure")),
            current => lifecycle.IsCurrent(current, _ => enabled),
            _ => throw new InvalidOperationException("A failed current operation has no result."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => currentOperation);
    }

    private static TaskCompletionSource<T> NewPending<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
