# Abraxius design system

The workstation uses a quiet, dark technical surface rather than a card dashboard. Tokens are centralized in `src/Abraxius.App/Styles/Theme.axaml`.

## Tokens

| Role | Use |
| --- | --- |
| Base | application background and graph canvas |
| Surface | rail, workspace, activity deck |
| Raised | context bar, inspector, command deck |
| Overlay | command palette and diagnostics overlay |
| Selection | focused list item or contextual emphasis |
| Accent | active/running state and command focus |
| Success | completed work |
| Warning | attention without failure |
| Error | actionable failure |

The surface scale is deliberately small. Spacing, text hierarchy, and subtle separators do most of the grouping work. Colored borders and permanent glows are not used as the primary hierarchy system.

## Typography and density

The default stack is `Cascadia Mono, DejaVu Sans Mono` for operational numbers and compact labels, with normal text weight used for descriptions. The shell is compact by default; `Compact`, `Comfortable`, and `Touch` are persisted semantic density choices. Touch mode is an interaction affordance, not a separate product visual language.

## State language

Every state has text and a shape/glyph in addition to color. Running is active teal, success is restrained green, warning is amber, and error is coral. Waiting and queued work remain neutral so the screen does not become a field of alarm colors.

## Motion rules

Motion communicates actual runtime changes. The current custom controls update from real snapshots; they do not generate synthetic activity. `ReducedMotion` is persisted and suppresses future animation work. Continuous render-thread animation and expensive blur are intentionally deferred until a measured need exists.
