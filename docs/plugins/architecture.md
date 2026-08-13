# Plugin architecture

Phase 22 keeps third-party code outside the workstation process:

```text
package -> inspect -> verify -> permission review -> immutable install
        -> PluginHost -> typed contributions -> Abraxius registries
```

One third-party plugin runs per `Abraxius.PluginHost`. The host uses a collectible `AssemblyLoadContext` and `AssemblyDependencyResolver` for dependency isolation. The main process handles only contracts, descriptors, IDs, and protobuf messages. Assembly-load isolation is not treated as a sandbox; Phase 17 and the operating-system process boundary remain authoritative.

Execution tiers are `BuiltIn`, `ManagedOutOfProcess`, `WasiSandboxed`, and policy-only `TrustedInProcess`. Managed out-of-process is the default and remains compatible with a Native-AOT main application.
