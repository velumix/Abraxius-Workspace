# Abraxius interaction model

Abraxius has one action system. Buttons, the command palette, future context menus, and shortcuts should resolve to `CommandDescriptor` instances registered in `CommandRegistry`; command logic is not duplicated by surface.

## Primary actions

* `Ctrl/Cmd+K` opens the fuzzy command palette.
* `Ctrl/Cmd+1..4` switches graph, lanes, agents, and activity views.
* `Ctrl/Cmd+Enter` submits the command deck.
* `Escape` closes the palette; the Cancel command cancels active execution.
* The command deck accepts natural-language intent. A future explicit `/` grammar can add runtime commands without guessing destructive intent.

The command palette is context-ready: the registry can filter by selected task or execution category as the inspector grows. The current command set covers mission execution/cancellation, view switching, terminal, diagnostics, layout density, and reduced motion.

## Attention model

Idle UI stays quiet. Running tasks are visible in the graph/lane views and `AttentionText` only escalates when failures/timeouts or live execution require it. A selected task reveals timing, dependencies, evidence, result, and error details in the inspector.

## Responsive composition

The shell uses viewport class rather than platform name. Expanded layouts keep rail, mission surface, inspector, activity deck, and command deck visible. Compact layouts suppress secondary panels and rely on mission/view navigation. Mobile and browser hosts reuse the same models and commands; only the host/lifetime and capability adapters differ.

## Terminal safety

The initial terminal abstraction accepts a direct executable plus arguments and routes through `IProcessExecutionService`. It does not silently select `bash`, `cmd`, PowerShell, or another shell. Shell-specific behavior belongs to an explicit Lattice capability.
