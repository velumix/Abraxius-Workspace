# Release security

Production release inputs are trusted machine metadata, not model output or release-note prose:

- protected version tags must match `build/Version.props`;
- release assets receive SHA-256 checksums;
- Windows and macOS artifacts are signed by protected CI credentials;
- Android signing identity is retained outside the repository;
- GitHub artifact attestations establish build provenance;
- published Stable assets are immutable; corrections create a new version;
- clients reject downgrades, wrong channels, and unsupported architectures;
- update credentials, signing keys, and tokens never enter logs or artifacts.

The updater uses HTTPS as transport but does not treat transport alone as authenticity. Package
verification, OS signatures, release identity, and provenance remain separate checks. Alternate
mirrors must be explicit configuration; chat, Lattice tools, release notes, and manifests cannot
change the trusted source or execute downloaded scripts.
