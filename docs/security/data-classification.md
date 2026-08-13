# Data classification

Security uses `Public`, `Internal`, `Confidential`, `Secret`, and `LocalOnly`. Derived context inherits the strongest relevant classification. `Secret` is not model egress content. `LocalOnly` cannot use external model/network resources.

Phase 6 routing now rejects non-local candidates for `Secret` and `LocalOnly` model requests. Memory, Debrief, notifications, plugins, and future artifacts must preserve scope and classification rather than treating summaries as declassification.
