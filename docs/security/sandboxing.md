# Sandboxing

Sandbox levels are `None`, `RestrictedProcess`, `IsolatedWorkspace`, `Container`, and `RemoteSandbox`. A Skill/plugin may request a minimum but cannot reduce policy requirements. If the required level is unavailable, execution is denied.

The implemented portable service honestly provides workspace isolation declarations. Git worktrees isolate mutations but are not OS sandboxes. Platform restricted-process/container adapters remain future platform work and must advertise actual guarantees before selection.
