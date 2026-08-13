# Design implementation workflow

`Implement selected` creates the existing Builder mission path. Its contract
requires preserving runtime behavior, building and testing the repository,
validating responsive interaction, and forbids copying provider HTML directly
into the application.

The selected `DesignCandidate` is passed by identity, not by “latest” lookup.
The candidate carries its source snapshot, provider reference, prompt snapshot,
markup reference, image reference when available, and Phase 18 artifact
reference. A later candidate cannot silently replace it.

The current first vertical slice is intentionally review-gated: generation and
artifact creation are automatic, while integration remains an explicit user
action. Argus/Phase 19 implementation validation is the next runtime mission
stage; Design Studio does not claim a generated candidate is implemented until
that mission reports success.
