# Changes
- Added snapshot-only recovery with visible status and bounded retry delays for the specific Windows notification event-subscription RPC failure. The initial notification baseline is preserved; recovery may miss notifications that appear and disappear between snapshots.
- Browser title matching now ignores WhatsApp unread-count prefixes, making saved WhatsApp tabs less likely to remain unresolved after the count changes.
- A manually saved wall cell stays in its position when its source window closes. A destroyed and reused HWND is treated as a new observed window lifetime.
- Choosing a live replacement checks that the window is still current and changes the slot only after its new DWM thumbnail registration succeeds.
