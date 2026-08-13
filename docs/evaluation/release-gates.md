# Release gates

Gates support absolute minimum/maximum, relative maximum regression, relative minimum improvement, and zero tolerance. Each gate defines severity and required sample size. Too few or incomparable samples yield Inconclusive.

ReleaseBlocking and SecurityCritical failures block release. Security critical escapes use explicit zero tolerance and are never averaged into a general score. An allowed non-security override records user and rationale without changing measured results. Critical-security override requires explicit Phase 17 policy; the normal API rejects it.
