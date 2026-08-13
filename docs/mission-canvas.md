# Mission Canvas

`ExecutionGraphView` and `ExecutionLanesView` render the same `UiGraphSnapshot` in different operational representations.

## Graph mode

The graph uses a stable left-to-right dependency layout. A task's column is derived from dependency depth and its row remains stable for a snapshot structure. State changes therefore recolor the work instead of constantly reshuffling the mission.

The renderer has three practical levels of detail:

* detailed nodes and labels for normal missions;
* compact status circles when zoomed out or when the graph grows;
* phase aggregates above 2,000 tasks, with the user invited to zoom into a narrower mission.

Edges are drawn once by the custom renderer. Selected-task edges and the computed longest dependency path are emphasized; unrelated edges are subdued or hidden in critical-path mode. Hit testing uses screen-space rectangles maintained by the renderer, not child controls.

Wheel zoom is centered on the pointer. Middle/right drag pans. The renderer culls nodes outside the viewport and does not create graph controls or text blocks for every task.

## Lanes mode

Lanes group actual task snapshots by `ExecutorKind`. A bar begins at the runtime's `StartedAt` timestamp and uses measured execution latency when available. Running bars use their current elapsed duration at render time. This makes overlap, queue separation, and executor utilization visible without presenting a project-management Gantt abstraction.

## Agent and activity modes

Agent mode groups task state by source/executor and exposes active task count and progress. Activity mode is a bounded typed block stream. Intent, planning, tool calls, evidence, verification, results, warnings, and errors remain distinguishable records instead of becoming a chat transcript.

## Accessibility fallback

The inspector, agent list, activity list, and event deck provide textual alternatives to the canvas. Selecting a task from the canvas updates the same `SelectedTask` model used by these controls. Keyboard view shortcuts are `Ctrl/Cmd+1..4`; touch hosts can use the same view buttons without hover or right-click.
