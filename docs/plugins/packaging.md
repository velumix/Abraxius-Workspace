# Packaging

The initial package is a NuGet-compatible ZIP containing the manifest, published managed payload, optional RID-native or WASI payloads, resources, license, and metadata. Installation hashes the complete archive, validates bounded entries and paths, extracts into staging, and atomically activates an immutable `PluginId/version/hash` directory. Plugins are not installed as references into Abraxius.
