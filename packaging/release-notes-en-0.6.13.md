# Changes

- Register and restore specific windows. Manually saved wall slots stay in place when their source windows close, so you can choose a replacement later.
- Restoration checks window titles and app identity before using a saved handle. Browser title matching ignores WhatsApp unread counts. If Windows destroys a window and reuses its handle, the app treats the replacement as a new window.
- Choosing a replacement verifies that the window is still current and keeps the existing thumbnail until the new one is registered.
- If a Windows notification subscription fails with the supported RPC error, notification attention can recover by reading snapshots. The app shows the recovery status and can miss notifications that disappear between checks.
- The Settings dialog grows to show longer notification status messages and keeps the Close button visible.
