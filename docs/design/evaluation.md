# Design evaluation

Design generation records duration, provider, source snapshot, candidate count,
and artifact references. Candidate quality is not reduced to a fabricated
“prettiness” score. A future `design.ui-regression` Phase 19 suite should
compare layout overflow, target sizes, keyboard contracts, frame/render work,
and responsive behavior before/after implementation.

Visual comparison must use the same capture profile and treat native Avalonia
text rasterization as an implementation detail. Structural and interaction
failures are more important than pixel equality.
