# Declarative UI

Plugins send `PluginViewDescriptor` and bounded view state. Abraxius owns rendering and styling. Components include text, forms, lists, virtualized tables, code, sanitized Markdown, images, metrics, and numeric charts. Buttons carry a registered `CommandId`; no plugin Avalonia control, runtime XAML, HTML, or script executes in the main process.

The Avalonia Extensions surface uses MVVM, compiled bindings, virtualized `ListBox` data, and coalesced updates so inactive or large plugin inventories do not block startup or flood the UI thread.
