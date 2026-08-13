# Policies

Policy layers are Global, User, Workspace, Project, Mission, Specialist, Skill, Plugin, and Temporary Grant. Explicit deny wins. An absent child rule is neutral; it cannot override a parent deny. Default deny handles unknown operations and resources.

Presets are deterministic presentation/configuration bundles:

- Conservative asks for local mutation.
- Balanced permits bounded Builder worktree mutation and direct approved development processes.
- Developer may broaden local project operations, but never raw secrets, force push, production effects, system paths, or external communication implicitly.

The policy tester uses the same evaluator without execution. Full traces contain rule results, not model reasoning.
