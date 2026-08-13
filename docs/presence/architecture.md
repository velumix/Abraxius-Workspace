# Presence architecture

Phase 16 separates process, runtime, and window lifetime. `DesktopApp` owns the one `AbraxiusRuntimeHost`; `MainWindow` is a restorable presentation surface. Desktop lifetime uses explicit shutdown, while close-to-tray hides the window. Explicit Quit checkpoints presence state, disposes the tray and view model, flushes/disposes the runtime, then requests application shutdown.

```text
Runtime → PresenceRuntime → AttentionPolicy → Needs You / NotificationHub → platform adapter
                   └──────→ BackgroundRuntimeCoordinator → tray state
```

Core Presence contains no Avalonia or native notification types. It observes high-level Agent Kernel state and never polls a model. Correctness remains in the scheduler, ledger, and durable Needs You store; OS delivery is best-effort.
