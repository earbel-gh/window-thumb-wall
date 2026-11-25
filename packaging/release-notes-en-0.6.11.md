# Changes

- Browser title matching now ignores WhatsApp unread-count prefixes, making saved WhatsApp tabs less likely to remain unresolved after the count changes.
- A manually saved wall cell stays in its position when its source window closes. A destroyed and reused HWND is treated as a new observed window lifetime.
- Choosing a live replacement checks that the window is still current and changes the slot only after its new DWM thumbnail registration succeeds.
