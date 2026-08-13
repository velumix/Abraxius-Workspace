# Workspaces

`WorkspacePolicy` is part of every specialist definition and assignment. Orion and Argus default to shared read-only workspaces. Daedalus defaults to shared mutable policy for a single controlled candidate; parallel candidates should request `IsolatedWorktree`.

`IWorkspaceIsolationService` is the host boundary for create/inspect/integrate/cleanup. `ManagedWorkspaceIsolationService` provides safe unique identities and refuses integration without a real Git host adapter and policy approval. It intentionally does not pretend to create a Git worktree. This prevents a model from silently mutating a repository while leaving a platform-specific Git adapter for a later integration.
