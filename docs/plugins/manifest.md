# Manifest

`abraxius.plugin.json` is static data and is parsed without loading an assembly. It pins schema, plugin ID and SemVer, publisher, application/API compatibility, platform/RID payloads, activation mode, permissions, contributions, dependencies, and entrypoints. Paths must be package-relative and traversal-free. Contribution IDs are plugin-local and become `plugin.id/contribution` globally.
