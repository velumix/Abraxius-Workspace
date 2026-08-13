# Versioning

Application version, `PluginApiVersion`, `PluginProtocolVersion`, plugin SemVer, and package hash are separate identities. API major changes may break source/binary compatibility; minor releases are additive. Protocol features are negotiated. New plugin versions install side-by-side, and active missions may pin an exact version.

The public contract projects enable .NET package validation. Stable releases should set `PackageValidationBaselineVersion` to the previous supported package in release CI.
