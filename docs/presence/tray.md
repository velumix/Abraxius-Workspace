# Tray

`ITrayService` is platform-neutral. The Avalonia desktop adapter owns one application-level `TrayIcon`, a bounded native menu, a generated monochrome Abraxius glyph, and six semantic states: idle, working, attention, error, update, and degraded. It restores the existing main window instead of creating another runtime.

Tray updates are driven by meaningful `PresenceSnapshot` changes. There is no animation or telemetry-frequency update loop. Unsupported shells fall back to normal window lifetime and in-app attention.
