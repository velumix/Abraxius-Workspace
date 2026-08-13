# AXL versioning and migrations

`AxlVersion.Current` is the only source of the current language version. The canonical header for the current release is `axl/1`.

- A major version changes grammar or semantics incompatibly and is rejected by the current parser.
- A minor version may add compatible syntax or schemas. A newer minor than the current implementation is rejected rather than silently reinterpreted.
- `AxlMigrationRegistry` provides the typed IR migration seam. Migrations should parse/decode a known old version, transform immutable IR, validate it, and then format/encode the target version.

Versioning applies independently to the AXL language and the Phase 2/Lattice protocols. AXL metadata can be carried in runtime graph metadata, but it does not replace protocol negotiation.
