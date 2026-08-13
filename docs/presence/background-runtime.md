# Background runtime

`IBackgroundRuntimeCoordinator` tracks window presence, active missions, pending human attention, and one of four modes: normal, reduced, pause noncritical, or pause all. Hidden is not paused. Mode changes control admission policy; they do not recreate work.

The coordinator is event-driven and exposes a checkpoint hook for quit, suspend, and future Phase 14 recovery integration. Mobile/browser hosts should checkpoint and reconnect when their operating system suspends local execution rather than promising unlimited background work.
