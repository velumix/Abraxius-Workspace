# Abraxius UI architecture

Phase 5 keeps the Avalonia application an observer and controller of the runtime. The scheduler owns execution correctness; the UI consumes a bounded event subscription and an optional metrics source.

```text
RuntimeEventHub (control + telemetry events)
             |
             v
RuntimeUiStateAggregator  -- lossy bounded subscription, 10,000 events
             |
             v
UiStateStore              -- task map, typed blocks, bounded event history
             |
             v
16 ms frame coalescer
             |
             v
UiGraphSnapshot (immutable presentation snapshot)
             |
       +-----+--------------------+
       |                          |
       v                          v
Avalonia bindings             custom render controls
shell / inspector /           ExecutionGraphView
virtualized lists             ExecutionLanesView
```

`RuntimeUiStateAggregator` never uses the Avalonia dispatcher for each backend event. It applies many backend events to `UiStateStore`, then posts at most one visual snapshot per frame with changes. The full-fidelity ledger remains independent of UI retention. The scheduler can continue if the window is minimized, closed, suspended, or not present.

## Presentation ownership

`UiTaskSnapshot`, `UiAgentCard`, `ActivityBlock`, and `UiGraphSnapshot` are presentation contracts. They are not runtime state and contain no scheduler locks or mutable provider objects. The Mission Canvas reads a snapshot and performs direct drawing; it does not query scheduler collections.

Normal Avalonia controls are used for the shell, command palette, inspector, terminal output, and bounded activity/event lists. The graph and lanes are single custom controls with direct hit testing. Large histories are retained as bounded data and presented through `ListBox` virtualization rather than one control per event.

## Threading boundaries

Runtime consumption and snapshot construction occur off the UI thread. `AvaloniaUiDispatcher` is used only to publish the finished snapshot and update bindings. Terminal lines are similarly marshalled only when they enter its bounded visual collection. File/process work is behind the platform process service and is never started by graph rendering.

## Host reuse

`MainView` is the shared root view. Desktop hosts provide a window and a desktop process adapter. Browser, embedded, mobile, and single-view hosts reuse the same root and report their own platform capability set. No core/runtime project references Avalonia.

## Deferred seams

The terminal surface is behind `ITerminalSurface`/`ITerminalSession`; the current desktop implementation invokes direct executables and deliberately does not assume a shell. A full terminal control and docking package remain replaceable integrations. Layout persistence currently stores semantic panel/view preferences; device-specific pixel layouts are not synchronized across platforms.
