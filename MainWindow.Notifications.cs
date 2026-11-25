using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowThumbWall;

internal enum AttentionVisualState
{
    None,
    Active,
    Red,
    Orange
}

internal enum NotificationAvailability
{
    Disabled,
    RequiresPackage,
    CheckingAccess,
    AccessRequired,
    AccessDenied,
    Listening,
    Unavailable,
    AccessFailed,
    SubscriptionFailed,
    ReadFailed,
    SyncFailed
}

internal static class NotificationAccessStatusPolicy
{
    internal static bool ShouldRequestAccess(UserNotificationListenerAccessStatus status) =>
        status == UserNotificationListenerAccessStatus.Unspecified;

    internal static NotificationAvailability GetAvailability(UserNotificationListenerAccessStatus status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => NotificationAvailability.Listening,
        UserNotificationListenerAccessStatus.Unspecified => NotificationAvailability.AccessRequired,
        UserNotificationListenerAccessStatus.Denied => NotificationAvailability.AccessDenied,
        _ => NotificationAvailability.AccessFailed
    };
}

internal sealed record NotificationAttentionGroup(
    uint NotificationId,
    AttentionVisualState VisualState,
    IReadOnlyList<IntPtr> CandidateHandles);

internal sealed record NotificationObservation(
    DateTimeOffset CreationTime,
    string Fingerprint,
    bool IsBaseline);

internal static class NotificationObservationPolicy
{
    internal static bool ShouldEvaluate(
        uint notificationId,
        NotificationObservation currentObservation,
        IReadOnlyDictionary<uint, NotificationObservation> knownObservations,
        IReadOnlyDictionary<uint, NotificationAttentionGroup> attentionGroups,
        IReadOnlySet<uint> dismissedNotificationIds)
    {
        if (dismissedNotificationIds.Contains(notificationId))
            return false;

        if (!knownObservations.TryGetValue(notificationId, out NotificationObservation? knownObservation))
            return true;

        bool changed =
            knownObservation.CreationTime != currentObservation.CreationTime ||
            !string.Equals(knownObservation.Fingerprint, currentObservation.Fingerprint, StringComparison.Ordinal);
        if (changed)
            return true;

        return !knownObservation.IsBaseline && !attentionGroups.ContainsKey(notificationId);
    }
}

internal sealed class NotificationSessionLifecycle<TSession> where TSession : class
{
    private TSession? _current;

    internal TSession? Current => _current;

    internal TSession Ensure(Func<TSession> create)
    {
        return _current ??= create();
    }

    internal bool IsCurrent(TSession session, Func<TSession, bool> isEnabled) =>
        ReferenceEquals(_current, session) && isEnabled(session);

    internal TSession? Invalidate(Action<TSession> deactivate)
    {
        TSession? previous = _current;
        _current = null;
        if (previous != null)
            deactivate(previous);
        return previous;
    }

    internal bool SubscribeIfCurrent(
        TSession session,
        Func<TSession, bool> isCurrent,
        Action subscribe,
        Action unsubscribe)
    {
        if (!isCurrent(session))
            return false;

        subscribe();
        if (isCurrent(session))
            return true;

        unsubscribe();
        return false;
    }

    internal async Task<bool> BaselineSubscribeReconcileIfCurrentAsync<TSnapshot>(
        TSession session,
        Func<TSession, bool> isCurrent,
        Func<Task<TSnapshot>> readInitialSnapshot,
        Action<TSnapshot> recordBaseline,
        Action subscribe,
        Action unsubscribe,
        Func<Task<TSnapshot>> readCurrentSnapshot,
        Action<TSnapshot> reconcile)
    {
        if (!isCurrent(session))
            return false;

        if (!await AwaitAndApplyIfCurrentAsync(
                session,
                readInitialSnapshot(),
                isCurrent,
                recordBaseline).ConfigureAwait(true))
            return false;

        if (!SubscribeIfCurrent(session, isCurrent, subscribe, unsubscribe))
            return false;

        if (!isCurrent(session))
        {
            unsubscribe();
            return false;
        }

        return await AwaitAndApplyIfCurrentAsync(session, readCurrentSnapshot(), isCurrent, reconcile)
            .ConfigureAwait(true);
    }

    internal async Task<bool> AwaitAndApplyIfCurrentAsync<TResult>(
        TSession session,
        Task<TResult> operation,
        Func<TSession, bool> isCurrent,
        Action<TResult> apply)
    {
        TResult result;
        try
        {
            result = await operation.ConfigureAwait(true);
        }
        catch
        {
            if (!isCurrent(session))
                return false;
            throw;
        }

        if (!isCurrent(session))
            return false;

        apply(result);
        return isCurrent(session);
    }
}

public partial class MainWindow
{
    private static readonly Color AttentionRed = Colors.Red;
    private static readonly Color AttentionOrange = Color.FromRgb(0xFF, 0x8C, 0x00);
    private const string NotificationDiagnosticsEnvironmentVariable = "WINDOWTHUMBWALL_NOTIFICATION_DIAGNOSTICS";
    private const int NotificationDiagnosticsMaxCharacters = 160;
    private const long NotificationDiagnosticsMaxBytes = 256 * 1024;
    private const int NotificationFollowUpSyncCount = 5;
    private static readonly TimeSpan NotificationAccessMonitorInterval = TimeSpan.FromSeconds(30);
    private static string NotificationDiagnosticsPath => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(AppState.GetStatePath()))!,
        "logs",
        "notification-attention.log");

    private readonly Dictionary<uint, NotificationAttentionGroup> _notificationAttentionGroups = [];
    private readonly Dictionary<uint, NotificationObservation> _notificationObservations = [];
    private readonly HashSet<uint> _dismissedNotificationIds = [];
    private readonly HashSet<IntPtr> _notificationResolvedWindows = [];
    private readonly HashSet<IntPtr> _notificationAmbiguousWindows = [];
    private readonly DispatcherTimer _notificationFollowUpTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _notificationAccessMonitorTimer = new() { Interval = NotificationAccessMonitorInterval };

    private readonly NotificationSessionLifecycle<NotificationSession> _notificationSessionLifecycle = new();
    private NotificationAvailability _notificationAvailability = NotificationAvailability.Disabled;

    private sealed class NotificationSession
    {
        internal UserNotificationListener? Listener { get; set; }
        internal TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs>? Handler { get; set; }
        internal bool Initialized { get; set; }
        internal bool Subscribed { get; set; }
        internal bool SyncInProgress { get; set; }
        internal bool SyncQueued { get; set; }
        internal bool BaselinePending { get; set; }
        internal int FollowUpRemainingTicks { get; set; }
    }

    private bool SupportsNotificationAttentionRuntime() => NativeMethods.HasCurrentPackageIdentity();

    private void InitializeNotificationFollowUpTimer()
    {
        _notificationFollowUpTimer.Tick += NotificationFollowUpTimer_Tick;
        _notificationAccessMonitorTimer.Tick += NotificationAccessMonitorTimer_Tick;
    }

    private void SetNotificationAttentionEnabled(bool enabled)
    {
        _notificationAttentionRequested = enabled;
        bool runtimeEnabled = enabled && SupportsNotificationAttentionRuntime();
        if (_notificationAttentionEnabled == runtimeEnabled)
        {
            if (enabled && !runtimeEnabled)
                SetNotificationAvailability(NotificationAvailability.RequiresPackage, "Notification listener requires packaged app identity for this run.");
            else if (!enabled)
                SetNotificationAvailability(NotificationAvailability.Disabled, "OS notification attention disabled by preference.");
            RequestStateSave();
            return;
        }

        _notificationAttentionEnabled = runtimeEnabled;
        if (runtimeEnabled)
        {
            NotificationSession session = EnsureNotificationSession();
            AppendNotificationDiagnostic("OS notification attention enabled.");
            SetNotificationAvailability(NotificationAvailability.CheckingAccess, "Notification listener access check started.");
            if (IsLoaded)
                InitializeNotificationListenerAsync(session);
        }
        else
        {
            ResetNotificationAttentionState();
            AppendNotificationDiagnostic("OS notification attention disabled.");
            SetNotificationAvailability(enabled ? NotificationAvailability.RequiresPackage : NotificationAvailability.Disabled,
                enabled ? "Notification listener requires packaged app identity for this run." : "OS notification attention disabled by preference.");
        }

        RequestStateSave();
    }

    private void ResetNotificationAttentionState()
    {
        DisposeNotificationListener();
        _notificationObservations.Clear();
        _dismissedNotificationIds.Clear();
        _notificationAttentionGroups.Clear();
        _notificationResolvedWindows.Clear();
        _notificationAmbiguousWindows.Clear();
        UpdateAllSlotAttentionVisuals();
    }

    private NotificationSession EnsureNotificationSession() =>
        _notificationSessionLifecycle.Ensure(static () => new NotificationSession());

    private void InitializeNotificationListenerAsync() =>
        InitializeNotificationListenerAsync(EnsureNotificationSession());

    private bool IsCurrentNotificationSession(object session) =>
        session is NotificationSession notificationSession &&
        _notificationSessionLifecycle.IsCurrent(notificationSession, _ => _notificationAttentionEnabled);

    private async void InitializeNotificationListenerAsync(NotificationSession session)
    {
        if (!IsCurrentNotificationSession(session))
            return;

        if (session.Initialized)
            return;

        session.Initialized = true;
        SetNotificationAvailability(NotificationAvailability.CheckingAccess, "Notification listener initialization started.");

        if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
        {
            SetNotificationAvailability(NotificationAvailability.Unavailable, "Notification listener type is not present on this system.");
            return;
        }

        if (!SupportsNotificationAttentionRuntime())
        {
            SetNotificationAvailability(NotificationAvailability.RequiresPackage, "Notification listener requires packaged app identity for this run.");
            return;
        }

        NotificationAvailability failureAvailability = NotificationAvailability.AccessFailed;
        string failingOperation = "notification listener access check";
        try
        {
            UserNotificationListener listener = UserNotificationListener.Current;
            session.Listener = listener;
            failingOperation = "notification listener access check";
            UserNotificationListenerAccessStatus accessStatus = listener.GetAccessStatus();
            AppendNotificationDiagnostic($"Notification listener access status before request: {accessStatus}");
            if (NotificationAccessStatusPolicy.ShouldRequestAccess(accessStatus))
            {
                SetNotificationAvailability(NotificationAvailability.AccessRequired, $"Notification listener access is {accessStatus}; requesting access.");
                failingOperation = "notification access request";
                failureAvailability = NotificationAvailability.AccessFailed;
                bool accessIsCurrent = await _notificationSessionLifecycle.AwaitAndApplyIfCurrentAsync(
                    session,
                    listener.RequestAccessAsync().AsTask(),
                    currentSession => IsCurrentNotificationSession(currentSession),
                    result => accessStatus = result);
                if (!accessIsCurrent)
                    return;
            }
            else if (accessStatus == UserNotificationListenerAccessStatus.Denied)
            {
                SetNotificationAvailability(NotificationAvailability.AccessDenied, "Notification access was denied; grant it in Windows Settings.");
                return;
            }

            if (!IsCurrentNotificationSession(session))
                return;

            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                SetNotificationAvailability(
                    NotificationAccessStatusPolicy.GetAvailability(accessStatus),
                    accessStatus == UserNotificationListenerAccessStatus.Denied
                        ? "Notification access was denied; grant it in Windows Settings."
                        : $"Notification listener access remains unresolved: {accessStatus}");
                return;
            }

            session.BaselinePending = true;
            failingOperation = "notification listener access check";
            failureAvailability = NotificationAvailability.AccessFailed;
            accessStatus = listener.GetAccessStatus();
            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                session.BaselinePending = false;
                SetNotificationAvailability(
                    NotificationAccessStatusPolicy.GetAvailability(accessStatus),
                    $"Notification listener access was not allowed before baseline: {accessStatus}");
                UnsubscribeNotificationSession(session);
                return;
            }
            bool baselineIsCurrent = await _notificationSessionLifecycle.BaselineSubscribeReconcileIfCurrentAsync(
                session,
                currentSession =>
                {
                    failingOperation = "notification listener access check";
                    failureAvailability = NotificationAvailability.AccessFailed;
                    return IsCurrentNotificationSession(currentSession) &&
                        (accessStatus = listener.GetAccessStatus()) == UserNotificationListenerAccessStatus.Allowed;
                },
                () =>
                {
                    failingOperation = "initial notification snapshot";
                    failureAvailability = NotificationAvailability.ReadFailed;
                    return listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask();
                },
                currentNotifications =>
                {
                    failureAvailability = NotificationAvailability.SyncFailed;
                    RecordNotificationBaseline(session, currentNotifications);
                },
                () =>
                {
                    failingOperation = "notification event subscription";
                    failureAvailability = NotificationAvailability.SubscriptionFailed;
                    session.Handler = (_, _) => Dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrentNotificationSession(session) || !session.Subscribed)
                            return;
                        ScheduleNotificationFollowUpSync(session);
                        QueueNotificationAttentionSync(session);
                    });
                    session.Subscribed = true;
                    listener.NotificationChanged += session.Handler;
                },
                () => UnsubscribeNotificationSession(session),
                () =>
                {
                    failingOperation = "current notification reconciliation snapshot";
                    failureAvailability = NotificationAvailability.ReadFailed;
                    return listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask();
                },
                currentNotifications =>
                {
                    failureAvailability = NotificationAvailability.SyncFailed;
                    ReconcileNotificationSnapshot(currentNotifications, "initialize-reconcile");
                });
            if (!baselineIsCurrent || !IsCurrentNotificationSession(session))
            {
                if (IsCurrentNotificationSession(session) && accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    SetNotificationAvailability(
                        NotificationAccessStatusPolicy.GetAvailability(accessStatus),
                        $"Notification listener access was not allowed during baseline: {accessStatus}");
                    DisposeNotificationListener();
                }
                return;
            }

            session.BaselinePending = false;
            SetNotificationAvailability(NotificationAvailability.Listening, "Notification listener baseline was recorded, subscription is active, and current snapshot was reconciled.");
            if (!_notificationAccessMonitorTimer.IsEnabled)
                _notificationAccessMonitorTimer.Start();
            if (session.SyncQueued)
                QueueNotificationAttentionSync(session);
        }
        catch (Exception ex)
        {
            if (IsCurrentNotificationSession(session))
            {
                session.BaselinePending = false;
                UnsubscribeNotificationSession(session);
                SetNotificationAvailability(failureAvailability, $"Notification listener {failingOperation} failed: {ex}");
            }
        }
    }

    private void RecordNotificationBaseline(NotificationSession session, IReadOnlyList<UserNotification> notifications)
    {
        if (!IsCurrentNotificationSession(session))
            return;

        _notificationObservations.Clear();
        _dismissedNotificationIds.Clear();
        foreach (UserNotification notification in notifications)
        {
            NotificationSignal signal = BuildNotificationSignal(notification, out _);
            _notificationObservations[notification.Id] = CreateNotificationObservation(notification, signal, isBaseline: true);
        }

        _notificationAttentionGroups.Clear();
        if (notifications.Count > 0)
        {
            List<NotificationWindowCandidate> candidates = CaptureMonitoredNotificationWindowCandidates();
            AppendNotificationSnapshot("initialize-baseline", notifications, candidates);
            AppendNotificationDiagnostic(
                $"Notification initialization recorded {notifications.Count} existing toast notification(s) without creating attention groups.");
        }
        else
        {
            AppendNotificationDiagnostic("Notification initialization completed with zero current toast notifications.");
        }

        RebuildNotificationAttentionIndex();
    }

    private void DisposeNotificationListener()
    {
        NotificationSession? current = _notificationSessionLifecycle.Current;
        if (current != null)
        {
            current.FollowUpRemainingTicks = 0;
            current.SyncQueued = false;
            current.BaselinePending = false;
        }
        _notificationFollowUpTimer.Stop();
        _notificationAccessMonitorTimer.Stop();
        _notificationSessionLifecycle.Invalidate(UnsubscribeNotificationSession);
    }

    private void UnsubscribeNotificationSession(NotificationSession session)
    {
        UserNotificationListener? listener = session.Listener;
        TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs>? handler = session.Handler;
        bool shouldUnsubscribe = session.Subscribed && listener != null && handler != null;
        session.Subscribed = false;
        session.Handler = null;
        session.Listener = null;
        session.FollowUpRemainingTicks = 0;
        session.SyncQueued = false;
        session.BaselinePending = false;
        try
        {
            if (shouldUnsubscribe)
                listener!.NotificationChanged -= handler!;
        }
        catch (Exception ex)
        {
            try
            {
                AppendNotificationDiagnostic($"Notification listener unsubscribe failed during teardown: {ex}");
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            session.SyncInProgress = false;
        }
    }

    private void NotificationAccessMonitorTimer_Tick(object? sender, EventArgs e)
    {
        NotificationSession? session = _notificationSessionLifecycle.Current;
        if (session == null || !IsCurrentNotificationSession(session) || !session.Subscribed || session.Listener == null)
        {
            _notificationAccessMonitorTimer.Stop();
            return;
        }

        UserNotificationListenerAccessStatus accessStatus;
        try
        {
            accessStatus = session.Listener.GetAccessStatus();
        }
        catch (Exception ex)
        {
            string diagnostic = $"Notification listener access monitor failed: {ex}";
            if (_notificationAvailability is NotificationAvailability.Listening or NotificationAvailability.AccessFailed)
                SetNotificationAvailability(NotificationAvailability.AccessFailed, diagnostic);
            else
                AppendNotificationDiagnostic(diagnostic);
            return;
        }

        if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
        {
            if (_notificationAvailability == NotificationAvailability.AccessFailed &&
                IsCurrentNotificationSession(session) && session.Subscribed && session.Listener != null)
            {
                SetNotificationAvailability(NotificationAvailability.Listening, "Notification access check recovered; the existing listener subscription remains active.");
            }
            return;
        }

        SetNotificationAvailability(
            NotificationAccessStatusPolicy.GetAvailability(accessStatus),
            $"Notification listener access is no longer allowed: {accessStatus}");
        ResetNotificationAttentionState();
    }

    private void NotificationFollowUpTimer_Tick(object? sender, EventArgs e)
    {
        NotificationSession? session = _notificationSessionLifecycle.Current;
        if (session == null || !IsCurrentNotificationSession(session) || session.Listener == null || session.FollowUpRemainingTicks <= 0)
        {
            _notificationFollowUpTimer.Stop();
            if (session != null)
                session.FollowUpRemainingTicks = 0;
            return;
        }

        session.FollowUpRemainingTicks--;
        QueueNotificationAttentionSync(session);

        if (session.FollowUpRemainingTicks <= 0)
            _notificationFollowUpTimer.Stop();
    }

    private void ScheduleNotificationFollowUpSync(NotificationSession session)
    {
        if (!IsCurrentNotificationSession(session))
            return;
        session.FollowUpRemainingTicks = NotificationFollowUpSyncCount;
        if (!_notificationFollowUpTimer.IsEnabled)
            _notificationFollowUpTimer.Start();
    }

    private async Task SyncNotificationAttentionAsync(NotificationSession session)
    {
        if (!IsCurrentNotificationSession(session) || session.Listener == null)
            return;
        UserNotificationListener listener = session.Listener;

        if (session.BaselinePending)
        {
            session.SyncQueued = true;
            return;
        }

        if (session.SyncInProgress)
        {
            session.SyncQueued = true;
            return;
        }

        session.SyncInProgress = true;
        NotificationAvailability failureAvailability = NotificationAvailability.AccessFailed;
        try
        {
            do
            {
                if (!IsCurrentNotificationSession(session))
                    return;
                session.SyncQueued = false;

                failureAvailability = NotificationAvailability.AccessFailed;
                UserNotificationListenerAccessStatus accessStatus = listener.GetAccessStatus();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    if (IsCurrentNotificationSession(session))
                    {
                        SetNotificationAvailability(
                            NotificationAccessStatusPolicy.GetAvailability(accessStatus),
                            $"Notification listener access is no longer allowed during sync: {accessStatus}");
                        ResetNotificationAttentionState();
                    }
                    return;
                }

                IReadOnlyList<UserNotification>? notifications = null;
                failureAvailability = NotificationAvailability.ReadFailed;
                accessStatus = UserNotificationListenerAccessStatus.Allowed;
                bool syncIsCurrent = await _notificationSessionLifecycle.AwaitAndApplyIfCurrentAsync(
                    session,
                    listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(),
                    currentSession => IsCurrentNotificationSession(currentSession) &&
                        (accessStatus = listener.GetAccessStatus()) == UserNotificationListenerAccessStatus.Allowed,
                    result => notifications = result);
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed && IsCurrentNotificationSession(session))
                {
                    SetNotificationAvailability(
                        NotificationAccessStatusPolicy.GetAvailability(accessStatus),
                        $"Notification listener access is no longer allowed during sync: {accessStatus}");
                    ResetNotificationAttentionState();
                    return;
                }
                if (!syncIsCurrent || notifications == null || !IsCurrentNotificationSession(session))
                    return;
                failureAvailability = NotificationAvailability.SyncFailed;
                ReconcileNotificationSnapshot(notifications);
                if (_notificationAvailability != NotificationAvailability.Listening)
                    SetNotificationAvailability(NotificationAvailability.Listening, "Notification sync completed while listener access remains allowed.");
            }
            while (IsCurrentNotificationSession(session) && session.SyncQueued);
        }
        catch (Exception ex)
        {
            if (IsCurrentNotificationSession(session))
            {
                bool accessCheckSucceeded = false;
                bool accessIsAllowed = false;
                UserNotificationListenerAccessStatus? observedAccessStatus = null;
                try
                {
                    observedAccessStatus = listener.GetAccessStatus();
                    accessIsAllowed = observedAccessStatus == UserNotificationListenerAccessStatus.Allowed;
                    accessCheckSucceeded = true;
                }
                catch (Exception)
                {
                }

                if (accessCheckSucceeded && !accessIsAllowed)
                {
                    SetNotificationAvailability(
                        NotificationAccessStatusPolicy.GetAvailability(observedAccessStatus!.Value),
                        $"Notification listener access was not allowed during sync; {ex}");
                    ResetNotificationAttentionState();
                }
                else
                {
                    SetNotificationAvailability(
                        accessCheckSucceeded ? failureAvailability : NotificationAvailability.AccessFailed,
                        $"Notification sync failed: {ex}");
                }
            }
        }
        finally
        {
            if (IsCurrentNotificationSession(session))
                session.SyncInProgress = false;
        }
    }

    private void ReconcileNotificationSnapshot(
        IReadOnlyList<UserNotification> notifications,
        string snapshotSource = "sync-delta")
    {
        HashSet<uint> currentIds = notifications.Select(static notification => notification.Id).ToHashSet();
        uint[] removedIds = _notificationObservations.Keys
            .Where(id => !currentIds.Contains(id))
            .ToArray();
        foreach (uint removedId in removedIds)
        {
            _notificationAttentionGroups.Remove(removedId);
            _notificationObservations.Remove(removedId);
            _dismissedNotificationIds.Remove(removedId);
        }

        List<(UserNotification Notification, NotificationSignal Signal)> changedNotifications = [];
        foreach (UserNotification notification in notifications)
        {
            NotificationSignal signal = BuildNotificationSignal(notification, out _);
            NotificationObservation currentObservation;
            if (_notificationObservations.TryGetValue(notification.Id, out NotificationObservation? knownObservation))
            {
                currentObservation = CreateNotificationObservation(
                    notification,
                    signal,
                    isBaseline: knownObservation.IsBaseline);

                if (knownObservation.CreationTime != currentObservation.CreationTime ||
                    !string.Equals(knownObservation.Fingerprint, currentObservation.Fingerprint, StringComparison.Ordinal))
                {
                    _dismissedNotificationIds.Remove(notification.Id);
                }
            }
            else
            {
                currentObservation = CreateNotificationObservation(notification, signal, isBaseline: false);
                _dismissedNotificationIds.Remove(notification.Id);
            }

            if (ShouldEvaluateNotification(notification.Id, currentObservation))
            {
                changedNotifications.Add((notification, signal));
                currentObservation = currentObservation with { IsBaseline = false };
            }

            _notificationObservations[notification.Id] = currentObservation;
        }

        if (changedNotifications.Count > 0)
        {
            List<NotificationWindowCandidate> candidates = CaptureMonitoredNotificationWindowCandidates();
            AppendNotificationSnapshot(
                snapshotSource,
                changedNotifications.Select(static entry => entry.Notification).ToArray(),
                candidates);
            foreach ((UserNotification notification, NotificationSignal signal) in changedNotifications)
            {
                _notificationAttentionGroups.Remove(notification.Id);
                AddNotificationAttentionGroup(notification, signal, candidates);
            }
        }

        if (notifications.Count == 0)
        {
            AppendNotificationDiagnostic("Notification sync completed with zero current toast notifications.");
        }
        else if (changedNotifications.Count == 0)
        {
            AppendNotificationDiagnostic(
                $"Notification sync observed no new or updated toast notifications. current={notifications.Count} removed={removedIds.Length}");
        }

        if (removedIds.Length > 0)
        {
            AppendNotificationDiagnostic(
                $"Notification sync removed attention groups for notification ids [{string.Join(", ", removedIds)}].");
        }

        RebuildNotificationAttentionIndex();
    }

    private void SetNotificationAvailability(NotificationAvailability availability, string diagnostic)
    {
        _notificationAvailability = availability;
        NotificationAvailability displayAvailability = _notificationAttentionRequested && !SupportsNotificationAttentionRuntime()
            ? NotificationAvailability.RequiresPackage
            : availability;
        _notificationSettingsWindow?.UpdateNotificationAvailability(
            LocalizedText.Get($"setting.osNotifications.status.{displayAvailability}"));
        AppendNotificationDiagnostic(diagnostic);
    }

    private List<NotificationWindowCandidate> CaptureMonitoredNotificationWindowCandidates()
    {
        List<NotificationWindowCandidate> candidates = [];
        foreach (ThumbnailSlot slot in _slots.Where(slot =>
                     slot.IsOccupied &&
                     slot.Binding is ThumbnailBinding binding &&
                     binding.IsObservedCurrent(_taskbarWindowOrder)))
        {
            candidates.Add(new NotificationWindowCandidate(
                Handle: slot.SourceHwnd,
                Title: NativeMethods.GetWindowTitle(slot.SourceHwnd),
                ProcessName: NativeMethods.GetProcessName(slot.SourceHwnd),
                ExecutablePath: NativeMethods.GetProcessImagePath(slot.SourceHwnd),
                AppUserModelId: NativeMethods.GetWindowAppUserModelId(slot.SourceHwnd)));
        }

        return candidates;
    }

    private void AddNotificationAttentionGroup(
        UserNotification notification,
        NotificationSignal signal,
        IReadOnlyList<NotificationWindowCandidate> candidates)
    {
        NotificationMatchResult result = NotificationWindowMatcher.Resolve(signal, candidates);
        if (result.Kind == NotificationMatchKind.None || result.CandidateHandles.Count == 0)
            return;

        if (result.Kind == NotificationMatchKind.Ambiguous)
        {
            if (NotificationAttentionPolicy.ShouldSuppressAmbiguousNotificationDueToFlash(
                    signal,
                    candidates,
                    _flashingWindows))
            {
                AppendNotificationDiagnostic(
                    $"Notification id={notification.Id} ambiguous attention suppressed because the same app already has an active flash signal.");
                return;
            }
        }

        AttentionVisualState state = result.Kind == NotificationMatchKind.Unique
            ? AttentionVisualState.Red
            : AttentionVisualState.Orange;

        _notificationAttentionGroups[notification.Id] = new NotificationAttentionGroup(
            notification.Id,
            state,
            result.CandidateHandles);
    }

    private static NotificationSignal BuildNotificationSignal(
        UserNotification notification,
        out string appInfoStatus)
    {
        List<string> texts = [];
        string appUserModelId = string.Empty;
        string appDisplayName = string.Empty;
        appInfoStatus = "available";

        try
        {
            NotificationBinding? binding =
                notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null)
            {
                foreach (var element in binding.GetTextElements())
                {
                    if (!string.IsNullOrWhiteSpace(element.Text))
                        texts.Add(element.Text);
                }
            }
        }
        catch
        {
            // Keep processing with whatever metadata is available.
        }

        try
        {
            var appInfo = notification.AppInfo;
            if (appInfo != null)
            {
                appUserModelId = appInfo.AppUserModelId ?? string.Empty;
                appDisplayName = appInfo.DisplayInfo?.DisplayName ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            appInfoStatus = $"unavailable:{ex.GetType().Name}";
        }

        return new NotificationSignal(
            AppUserModelId: appUserModelId,
            AppDisplayName: appDisplayName,
            NotificationTexts: texts);
    }

    private void AppendNotificationSnapshot(
        string phase,
        IReadOnlyList<UserNotification> notifications,
        IReadOnlyList<NotificationWindowCandidate> candidates)
    {
        if (!ShouldWriteNotificationDiagnostics())
            return;

        StringBuilder builder = new();
        int monitoredCount = _slots.Count(static slot => slot.IsOccupied);
        builder.AppendLine(
            $"Notification snapshot phase={phase} notifications={notifications.Count} candidates={candidates.Count} monitored={monitoredCount}");

        if (monitoredCount > 0)
        {
            builder.AppendLine("Monitored slots:");
            foreach (ThumbnailSlot slot in _slots.Where(static slot => slot.IsOccupied))
            {
                builder.AppendLine(
                    $"  slot hwnd={FormatHandle(slot.SourceHwnd)} title=\"{SanitizeNotificationDiagnosticValue(slot.SourceTitle)}\" process=\"{SanitizeNotificationDiagnosticValue(slot.SourceProcessName)}\"");
            }
        }

        builder.AppendLine("Candidate windows:");
        foreach (NotificationWindowCandidate candidate in candidates)
        {
            bool monitored = _slots.Any(slot => slot.IsOccupied && slot.SourceHwnd == candidate.Handle);
            builder.AppendLine(
                $"  hwnd={FormatHandle(candidate.Handle)} monitored={monitored} title=\"{SanitizeNotificationDiagnosticValue(candidate.Title)}\" process=\"{SanitizeNotificationDiagnosticValue(candidate.ProcessName)}\" exe=\"{SanitizeNotificationDiagnosticValue(Path.GetFileName(candidate.ExecutablePath))}\" aumid=\"{SanitizeNotificationDiagnosticValue(candidate.AppUserModelId)}\"");
        }

        foreach (UserNotification notification in notifications)
        {
            NotificationSignal signal = BuildNotificationSignal(notification, out string appInfoStatus);
            NotificationMatchResult result = NotificationWindowMatcher.Resolve(signal, candidates);

            builder.AppendLine(
                $"Notification id={notification.Id} appInfo={appInfoStatus} app=\"{SanitizeNotificationDiagnosticValue(signal.AppDisplayName)}\" aumid=\"{SanitizeNotificationDiagnosticValue(signal.AppUserModelId)}\" texts=\"{string.Join(" | ", signal.NotificationTexts.Select(SanitizeNotificationDiagnosticValue))}\" result={result.Kind} handles=[{string.Join(", ", result.CandidateHandles.Select(FormatHandle))}]");

            foreach (IntPtr handle in result.CandidateHandles)
            {
                NotificationWindowCandidate? candidate = candidates.FirstOrDefault(window => window.Handle == handle);
                if (candidate != null)
                {
                    bool monitored = _slots.Any(slot => slot.IsOccupied && slot.SourceHwnd == handle);
                    builder.AppendLine(
                        $"    matched hwnd={FormatHandle(candidate.Handle)} monitored={monitored} title=\"{SanitizeNotificationDiagnosticValue(candidate.Title)}\" process=\"{SanitizeNotificationDiagnosticValue(candidate.ProcessName)}\" exe=\"{SanitizeNotificationDiagnosticValue(Path.GetFileName(candidate.ExecutablePath))}\" aumid=\"{SanitizeNotificationDiagnosticValue(candidate.AppUserModelId)}\"");
                }
            }
        }

        AppendNotificationDiagnostic(builder.ToString().TrimEnd());
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static string CreateNotificationFingerprint(NotificationSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return string.Join(
            "\u001F",
            signal.AppUserModelId,
            signal.AppDisplayName,
            string.Join("\u001E", signal.NotificationTexts));
    }

    private static NotificationObservation CreateNotificationObservation(
        UserNotification notification,
        NotificationSignal signal,
        bool isBaseline) =>
        new(
            notification.CreationTime,
            CreateNotificationFingerprint(signal),
            isBaseline);

    private bool ShouldEvaluateNotification(uint notificationId, NotificationObservation currentObservation)
        => NotificationObservationPolicy.ShouldEvaluate(
            notificationId,
            currentObservation,
            _notificationObservations,
            _notificationAttentionGroups,
            _dismissedNotificationIds);

    private static bool ShouldWriteNotificationDiagnostics()
    {
        string? value = Environment.GetEnvironmentVariable(NotificationDiagnosticsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeNotificationDiagnosticValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string collapsed = string.Join(" ", value.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= NotificationDiagnosticsMaxCharacters)
            return collapsed;

        return collapsed[..(NotificationDiagnosticsMaxCharacters - 3)] + "...";
    }

    private static void RotateNotificationDiagnosticsIfNeeded()
    {
        if (!File.Exists(NotificationDiagnosticsPath))
            return;

        FileInfo logFile = new(NotificationDiagnosticsPath);
        if (logFile.Length < NotificationDiagnosticsMaxBytes)
            return;

        string archivedPath = NotificationDiagnosticsPath + ".1";
        if (File.Exists(archivedPath))
            File.Delete(archivedPath);

        File.Move(NotificationDiagnosticsPath, archivedPath);
    }

    private static void AppendNotificationDiagnostic(string message)
    {
        if (!ShouldWriteNotificationDiagnostics())
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotificationDiagnosticsPath)!);
            RotateNotificationDiagnosticsIfNeeded();
            string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}";
            File.AppendAllText(NotificationDiagnosticsPath, line, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never break attention handling.
        }
    }

    private void QueueNotificationAttentionSync()
    {
        NotificationSession? session = _notificationSessionLifecycle.Current;
        if (session == null)
            return;

        QueueNotificationAttentionSync(session);
    }

    private void QueueNotificationAttentionSync(NotificationSession session)
    {
        if (!IsCurrentNotificationSession(session) || !session.Subscribed)
            return;

        Dispatcher.BeginInvoke(async () =>
        {
            if (IsCurrentNotificationSession(session))
                await SyncNotificationAttentionAsync(session);
        });
    }

    private void ClearNotificationAttentionGroupsForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _notificationAttentionGroups.Count == 0)
            return;

        uint[] groupsToClear = _notificationAttentionGroups
            .Where(pair => pair.Value.CandidateHandles.Contains(hwnd))
            .Select(static pair => pair.Key)
            .ToArray();

        if (groupsToClear.Length == 0)
            return;

        foreach (uint groupId in groupsToClear)
        {
            _notificationAttentionGroups.Remove(groupId);
            _dismissedNotificationIds.Add(groupId);
        }

        RebuildNotificationAttentionIndex();
    }

    private void RebuildNotificationAttentionIndex()
    {
        _notificationResolvedWindows.Clear();
        _notificationAmbiguousWindows.Clear();

        foreach (NotificationAttentionGroup group in _notificationAttentionGroups.Values)
        {
            foreach (IntPtr hwnd in group.CandidateHandles)
            {
                if (group.VisualState == AttentionVisualState.Red)
                {
                    _notificationResolvedWindows.Add(hwnd);
                    _notificationAmbiguousWindows.Remove(hwnd);
                }
                else if (!_notificationResolvedWindows.Contains(hwnd))
                {
                    _notificationAmbiguousWindows.Add(hwnd);
                }
            }
        }

        UpdateAllSlotAttentionVisuals();
    }

    private void UpdateAllSlotAttentionVisuals()
    {
        for (int i = 0; i < _slots.Count; i++)
            UpdateSlotAttentionVisual(i);
    }

    private void UpdateSlotAttentionVisual(int idx)
    {
        if (idx < 0 || idx >= _slots.Count || idx >= _slotAttentionVisualStates.Count)
            return;

        AttentionVisualState desiredState = GetDesiredSlotAttentionState(idx);
        if (_slotAttentionVisualStates[idx] == desiredState)
            return;

        _slotAttentionVisualStates[idx] = desiredState;
        switch (desiredState)
        {
            case AttentionVisualState.Active:
                ShowActiveBorder(idx);
                break;
            case AttentionVisualState.Red:
                StartFlashBorder(idx, AttentionRed);
                break;
            case AttentionVisualState.Orange:
                StartFlashBorder(idx, AttentionOrange);
                break;
            default:
                StopFlashBorder(idx);
                break;
        }
    }

    private AttentionVisualState GetDesiredSlotAttentionState(int idx)
    {
        if (idx < 0 || idx >= _slots.Count || !_slots[idx].IsOccupied)
            return AttentionVisualState.None;

        IntPtr hwnd = _slots[idx].SourceHwnd;
        if (_flashingWindows.Contains(hwnd) || _notificationResolvedWindows.Contains(hwnd))
            return AttentionVisualState.Red;

        if (_notificationAmbiguousWindows.Contains(hwnd))
            return AttentionVisualState.Orange;

        return hwnd == _activeSourceHwnd
            ? AttentionVisualState.Active
            : AttentionVisualState.None;
    }
}
