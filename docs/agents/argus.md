# Argus

Argus is the independent verifier for `SpecialistRole.Verifier`.

Argus receives the success contract and candidate evidence, then runs a separate verification graph. The current runtime adapter uses the existing verification executor; richer test, diff, invariant, and security adapters can be registered without changing the Agent Kernel. Daedalus’s report is not treated as proof.
