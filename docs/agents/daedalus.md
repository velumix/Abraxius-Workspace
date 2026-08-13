# Daedalus

Daedalus is the builder for `SpecialistRole.Builder`.

He receives the objective, success criteria, evidence references, and a Phase 10 compiled context. The current scheduler adapter sends that curated context to the configured Phase 6 model and returns an implementation proposal. A proposed mutation does not execute merely because a model emitted it. Repository changes must pass policy/Lattice and an explicit workspace/integration adapter.

Daedalus never produces the final verification authority. Builder assignments that require verification are followed by Argus.
