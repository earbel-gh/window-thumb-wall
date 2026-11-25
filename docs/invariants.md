# Invariants

These rules define the non-negotiable project constraints for WindowThumbWall.

## Packaging

- Official distribution formats are limited to ZIP, MSI, and MSIX.
- Changes to packaging, CI, documentation, or release flow must preserve support for all three formats.

## Release provenance

- Only CI-built release artifacts are distributed.
- Tag-driven GitHub Actions builds are the source of truth for release packages.
- Locally built binaries are for development and validation only.

## Product scope

- The application displays live DWM thumbnails of external windows.
- It is not a screen-capture or streaming application.

## Window wall state

- Rule IDs identify the user's specific auto-add choices. Slot IDs identify ordered wall locations. A slot can survive loss of its current window, and a registration can outlive removal of its wall slot.
- Keep the chosen title and identity as restoration anchors. Live HWND, process lifetime and observed title are runtime evidence. A successful automatic match can update last-binding evidence while preserving the chosen anchors.
- Register the replacement DWM thumbnail before dropping the previous registration or changing the target. Apply deferred selection and menu actions to their current slot ID.
- Protect unreadable settings, keep the original bytes before schema migration, and require the loaded file revision to match before writing. A stale instance can export its changes to a new recovery file.

## Platform

- Windows is the supported runtime platform.
- The desktop application stack is WPF on .NET 10.
