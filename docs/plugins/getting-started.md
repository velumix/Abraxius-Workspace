# Getting started

A managed plugin references only `Abraxius.Plugin.Contracts`, `Abraxius.Plugin.Managed.Abstractions`, and `Abraxius.Plugin.Sdk`. Implement `IAbraxiusPlugin`, return a static `PluginRegistration`, declare every permission and contribution in `abraxius.plugin.json`, then package the published payload into a NuGet-compatible archive.

Validate before execution with `abraxius plugins validate package.nupkg`. Unsigned development packages require Developer Mode; ordinary installation rejects them.
