# Changes
- Register and restore specific windows. Manually saved wall slots stay in place when their source closes, so you can choose a replacement later.
- Restoring a saved window checks meaningful titles and app identity before trusting a saved window handle. Browser title matching ignores WhatsApp unread counts.
- Choosing a replacement verifies that the window is still current and keeps the existing thumbnail until the new one is registered.
- On Windows systems affected by the notification subscription error, notification attention can recover by checking snapshots. Notifications that disappear between checks may be missed.
- The Settings dialog grows to show longer notification status messages and keeps the Close button visible.
