# Surface capture and source context

Surfaces are registered by stable `DesignSurfaceId`, not by filename search.
Each descriptor declares relevant source files, required interactions,
responsive profiles, and forbidden/required design constraints.

`FileSystemDesignSourceResolver` reads only declared files below the configured
workspace root, bounds source snippets, and records SHA-256 content hashes in a
`DesignSourceSnapshot`.

Synthetic capture is the default for Design Studio and deliberately produces no
fake bitmap. This prevents private live conversation content from being sent
to a cloud provider merely because the user asked for a redesign. When an
explicit live capture is requested from an attached Avalonia window,
`AvaloniaDesignSurfaceCapture` uses `RenderTargetBitmap` and records the actual
rendered dimensions. A requested 1920x1080 target is not falsely reported when
the attached window rendered at another size.

Deterministic offscreen fixture rendering remains an extension point for the
registered surface fixture providers. Until a fixture is registered, an
unavailable capture remains unavailable rather than becoming a placeholder
image.
