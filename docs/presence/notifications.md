# Notifications

Runtime events become typed `AbraxiusNotification` candidates. `DefaultAttentionPolicy` chooses none, in-app, native, or critical delivery based on category, focus, permission, privacy, quiet hours, and user settings. The hub applies two-minute key deduplication, a per-minute native rate limit, and bounded history.

Mission completion, terminal verification failure, blocked work, update readiness, meaningful connection degradation, and security events are eligible. Tool calls, searches, memory retrieval, delegation, and routine progress are not.

Native delivery is best-effort and never canonical state. Linux/macOS desktop adapters use the platform notification command without a shell; unsupported providers degrade to in-app attention.
