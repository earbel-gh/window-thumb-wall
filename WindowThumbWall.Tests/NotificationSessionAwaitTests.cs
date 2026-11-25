using Windows.UI.Notifications.Management;

namespace WindowThumbWall.Tests;

public sealed class NotificationSessionAwaitTests
{
    [Theory]
    [InlineData(UserNotificationListenerAccessStatus.Unspecified, nameof(NotificationAvailability.AccessRequired), true)]
    [InlineData(UserNotificationListenerAccessStatus.Denied, nameof(NotificationAvailability.AccessDenied), false)]
    [InlineData(UserNotificationListenerAccessStatus.Allowed, nameof(NotificationAvailability.Listening), false)]
    [InlineData((UserNotificationListenerAccessStatus)99, nameof(NotificationAvailability.AccessFailed), false)]
    public void AccessStatusPolicy_RequestsOnlyUnspecifiedAndMapsDenialSeparately(
        UserNotificationListenerAccessStatus accessStatus,
        string expectedAvailability,
        bool shouldRequest)
    {
        Assert.Equal(shouldRequest, NotificationAccessStatusPolicy.ShouldRequestAccess(accessStatus));
        Assert.Equal(expectedAvailability, NotificationAccessStatusPolicy.GetAvailability(accessStatus).ToString());
    }

    [Theory]
    [InlineData(unchecked((int)0x800706BE), true)]
    [InlineData(unchecked((int)0x800706BA), false)]
    [InlineData(unchecked((int)0x80040154), false)]
    public void SnapshotRecoveryPolicy_OnlyLatchesVerifiedSubscriptionFailure(int hresult, bool expected)
    {
        Assert.Equal(expected, NotificationSnapshotRecoveryPolicy.ShouldRecoverFromSubscriptionFailure(hresult));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    [InlineData(12, 30)]
    public void SnapshotRecoveryPolicy_UsesCappedDeterministicBackoff(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), NotificationSnapshotRecoveryPolicy.GetRetryDelay(failures));
    }

    [Fact]
    public void ReenabledSnapshotRecovery_RecordsBaselineBeforeReconcilingLaterSnapshot()
    {
        List<string> actions = [];
        int[] baseline = [1, 2];
        int[] laterSnapshot = [1, 2, 3];

        bool baselineWasRecorded = NotificationSnapshotRecoveryPolicy.ApplySnapshot(
            baselinePending: true,
            snapshot: baseline,
            snapshot => actions.Add($"baseline:{snapshot.Length}"),
            _ => actions.Add("unexpected-reconcile"));
        bool laterSnapshotReconciledAsDelta = NotificationSnapshotRecoveryPolicy.ApplySnapshot(
            baselinePending: false,
            snapshot: laterSnapshot,
            _ => actions.Add("unexpected-baseline"),
            snapshot => actions.Add($"reconcile:{snapshot.Length}"));

        Assert.True(baselineWasRecorded);
        Assert.False(laterSnapshotReconciledAsDelta);
        Assert.Equal(["baseline:2", "reconcile:3"], actions);
    }

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

    [Fact]
    public async Task BaselineThenSubscribeThenReconcile_PreservesInterveningNotifications()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        bool enabled = true;
        FakeSession session = lifecycle.Ensure(static () => new FakeSession());
        TaskCompletionSource<int[]> pendingCurrentSnapshot = NewPending<int[]>();
        List<string> order = [];
        List<int> knownBaselineIds = [];
        List<int> newNotificationIds = [];

        Task<bool> baseline = lifecycle.BaselineSubscribeReconcileIfCurrentAsync(
            session,
            current => lifecycle.IsCurrent(current, _ => enabled),
            () =>
            {
                order.Add("initial-snapshot");
                return Task.FromResult(new[] { 10 });
            },
            static _ => true,
            initialIds =>
            {
                knownBaselineIds.AddRange(initialIds);
                order.Add("record-baseline");
            },
            () =>
            {
                order.Add("subscribe");
                session.Subscribe();
            },
            session.Unsubscribe,
            () =>
            {
                Assert.True(session.IsSubscribed);
                order.Add("current-snapshot");
                return pendingCurrentSnapshot.Task;
            },
            currentIds =>
            {
                order.Add("reconcile-current");
                newNotificationIds.AddRange(currentIds.Except(knownBaselineIds));
            });

        Assert.Equal(["initial-snapshot", "record-baseline", "subscribe", "current-snapshot"], order);
        Assert.Empty(newNotificationIds);
        pendingCurrentSnapshot.SetResult([10, 20]);

        Assert.True(await baseline);
        Assert.Equal(["initial-snapshot", "record-baseline", "subscribe", "current-snapshot", "reconcile-current"], order);
        Assert.Equal([10], knownBaselineIds);
        Assert.Equal([20], newNotificationIds);
        Assert.Equal(1, session.SubscribeCount);
    }

    [Fact]
    public async Task SyncResultCompletedAfterAccessRevocation_IsNotApplied()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        bool enabled = true;
        bool accessAllowed = true;
        FakeSession session = lifecycle.Ensure(static () => new FakeSession());
        TaskCompletionSource<int> pendingSync = NewPending<int>();

        Task<bool> sync = lifecycle.AwaitAndApplyIfCurrentAsync(
            session,
            pendingSync.Task,
            current => lifecycle.IsCurrent(current, _ => enabled && accessAllowed),
            result => session.AppliedResultCount += result);

        accessAllowed = false;
        pendingSync.SetResult(5);

        Assert.False(await sync);
        Assert.Equal(0, session.AppliedResultCount);
    }

    [Fact]
    public async Task BaselineFailureFromCurrentSession_PropagatesForAvailabilityReporting()
    {
        var lifecycle = new NotificationSessionLifecycle<FakeSession>();
        FakeSession session = lifecycle.Ensure(static () => new FakeSession());

        Task<bool> baseline = lifecycle.BaselineSubscribeReconcileIfCurrentAsync(
            session,
            current => lifecycle.IsCurrent(current, static _ => true),
            () => Task.FromResult(1),
            static _ => true,
            _ => { },
            session.Subscribe,
            session.Unsubscribe,
            () => Task.FromException<int>(new InvalidOperationException("snapshot unavailable")),
            result => session.AppliedResultCount += result);

        await Assert.ThrowsAsync<InvalidOperationException>(() => baseline);
        Assert.Equal(0, session.AppliedResultCount);
    }

    private static TaskCompletionSource<T> NewPending<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
