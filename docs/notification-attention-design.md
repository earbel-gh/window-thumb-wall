# Notification Attention Matching

This document describes how WindowThumbWall expands attention signaling beyond the existing taskbar flash hook.

## Goals
- Keep the current `HSHELL_FLASH` path as the exact, HWND-based attention signal.
- Add Windows notification monitoring as a secondary signal source.
- Only react to newly added or updated Windows toast notifications.
- Resolve a notification to a single window when the evidence is strong enough.
- If a notification cannot be resolved to a single monitored window in the same source app, highlight every monitored candidate in that app instead.
- Clear the notification-derived highlight when any candidate window is activated.

## User Setting
- `Reflect OS notifications` controls whether Windows notifications participate in attention highlighting.
- Default: off
- Persistence: stored alongside the normal app state in the same `state.json`
- UI: changed from the main pane through the dedicated Settings window opened by the `Settings` button under `Fullscreen`
- Runtime requirement: the feature is available only when WindowThumbWall runs with packaged app identity, for example from the installed MSIX build

## Design Principles
- Treat notification matching as source-app-scoped candidate narrowing, not fallback expansion.
- Only create notification attention from source-app evidence that is present on the notification itself.
- Separate exact signals from inferred signals:
  - `HSHELL_FLASH` is exact because Windows gives an `HWND`.
  - notification matching is inferred because Windows does not give an `HWND`.
- Compare like with like:
  - notification text tokens only compare against window title tokens
  - `AppUserModelId` only compares against window `AppUserModelId`
  - app-display and identity hints only compare against process name or executable base name
- Prefer exact equality over substring matching.
- If the source app cannot be identified, do not render notification attention.
- If the evidence does not narrow to one monitored window inside the same source app, keep the result ambiguous.

## Signal Sources
- Taskbar flash:
  - Source: shell hook `HSHELL_FLASH`
  - Precision: exact HWND
  - Visual: red flashing border
- Windows notification listener:
  - Source: `UserNotificationListener`
  - Precision: app metadata plus notification text, not HWND
  - Collection model: record an initial toast snapshot as the baseline, subscribe to change events, reconcile one current-session snapshot against the baseline, then use event-triggered sync plus a 5-second follow-up reconciliation window
  - Visual:
    - red flashing border for a unique match
    - orange flashing border for ambiguous monitored candidates

## Listener Status And Session Lifecycle

Settings reports the notification listener state as one of: `Disabled`, `RequiresPackage`, `CheckingAccess`, `AccessRequired`, `AccessDenied`, `Listening`, `Unavailable`, `AccessFailed`, `SubscriptionFailed`, `ReadFailed`, or `SyncFailed`. `AccessDenied` also covers access revoked after listening began. `AccessFailed` identifies an access query or request failure; `SubscriptionFailed` an event-handler subscription failure; `ReadFailed` a notification snapshot read failure; and `SyncFailed` a snapshot reconciliation failure. The open Settings dialog reflects status changes while the listener runs.

Initialization first takes a snapshot and records it as the baseline, then subscribes to notification-change events and takes one current-session snapshot to reconcile against that baseline. A notification created between the baseline read and subscription is therefore included by the second read. The baseline snapshot itself does not create attention. An event queued during the reconciliation read can trigger a later sync as usual.

Each asynchronous sync is tied to its current listener session and rechecks access around the toast-list read. Results from an ended session are ignored. If access is revoked, notification attention is cleared and the listener is torn down. Teardown stops the follow-up timer and invalidates session and handler state before attempting native event removal; unsubscribe errors are contained and logged.

While listening, event-driven reconciliation and its short follow-up window remain the normal collection path. A 30-second access monitor checks only `GetAccessStatus()` to detect permission revocation; it does not read the toast list. The listener does not poll the toast list continuously.

## Candidate Pool
Candidate matching uses monitored slots only.

Reasoning:
- notification attention exists to decorate the wall, so only wall cells are relevant targets
- keeping the candidate pool inside monitored slots prevents cross-app and off-wall evidence from driving orange highlights

## Match Order
The resolver evaluates notification evidence in this order.

1. If the notification does not expose source-app identity, do not match it to any window
2. Narrow monitored candidates to the same source app first:
   - exact `AppUserModelId` match
   - otherwise exact process-name / executable-base-name match from app-identity hints
3. If no monitored candidate survives that reduction, do not match the notification to any window
4. Within the surviving same-app monitored set, narrow by strong notification-text token to window-title token exact match, choosing the narrowest non-empty candidate set
5. If one candidate remains, treat it as unique
6. If multiple candidates remain, treat the result as ambiguous

The resolver intentionally does not use notification ID or app display name as a unique window identifier.
App display name is only a reduction hint and must not create substring matches against unrelated window titles or full paths.

## Comparison Semantics
- Notification text to title:
  - normalize both sides
  - split into tokens
  - compare by token equality only
  - do not use substring, prefix, suffix, or full-path contains matching
- `AppUserModelId`:
  - normalize both sides
  - compare by exact equality only
- Executable reduction:
  - derive hint tokens from app display name and `AppUserModelId`
  - compare those hints only to process name and executable base name
  - do not compare them to full executable paths or window titles
- Candidate selection:
  - each step may only shrink the candidate set
  - title matching only runs inside the app-compatible monitored candidate set
  - if no app-compatible monitored candidates exist, the result is `None`
  - if multiple title tokens match, keep the smallest non-empty set

## Visual State Rules
- Red:
  - taskbar flash is active for the monitored window
  - or notification matching resolved that notification to one monitored window
- Orange:
  - notification matching left multiple monitored candidates
  - but only when the same source app does not already have a monitored window flashing red from the taskbar at the time that notification is added
- White:
  - the source window for that monitored slot is the current foreground window
  - this border does not flash
  - red and orange attention states take priority over white
- No border:
  - no active taskbar flash
  - and no active notification-derived attention group touching that slot

If a monitored window is both taskbar-flashing and notification-matched, red wins.
If an ambiguous notification belongs to an app that already has any monitored window flashing red, suppress the orange notification border for that notification instead of rendering both signals.

## Clearing Rules
- Taskbar flash clears on foreground activation, same as before.
- Notification-derived attention groups clear when any candidate window in that group becomes active.
- After the user clears a notification-derived attention group by activating a candidate window, that same unchanged toast does not light up again until it changes or disappears.
- Only notifications that are new or changed since the initial baseline create or refresh attention groups. Notifications already present in the baseline are not replayed unless their contents or creation time later change.
- Notifications first seen after the baseline remain eligible for re-evaluation on later syncs until they either resolve into an attention group or disappear from the current toast list.
- Ambiguous flash suppression is evaluated when a newly added or updated notification is processed.
- Because `UserNotificationChangedKind` only reports add/remove events, a notification event opens a 5-second follow-up reconciliation window to catch near-term in-place updates to an existing toast without constant polling.
- Outside that event-driven follow-up window, the installed MSIX build does not continuously poll the toast list.
- Unmonitored windows do not participate in notification matching.

## Packaging Requirements
- MSIX manifest declares `uap3:Capability Name="userNotificationListener"`.
- Local MSIX builds are signed with a reusable self-signed test certificate so they can be installed for testing.

## Known Limits
- Notification matching is deterministic narrowing over incomplete evidence, not a direct OS-provided window identifier.
- Notifications without usable source-app metadata are ignored instead of guessed.
- Multiple windows with the same app identity and weak notification text will remain ambiguous.
- Notification listener behavior depends on notification access being granted to the installed app package.
