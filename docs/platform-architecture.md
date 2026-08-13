# Platform architecture

Phase 3 makes platform compatibility a capability contract rather than a collection of operating-system branches.

```text
                    Shared Abraxius
             Protocol + Core + Runtime + App
                              │
                    Abraxius.Platform
       environment · capabilities · budgets · lifecycle
                              │
        ┌─────────────┬───────┼────────┬─────────────┐
        ▼             ▼       ▼        ▼             ▼
     Desktop       Android   iOS    Browser      Embedded
     host          host      host   host         host seam
```

## Project boundaries

`Abraxius.Protocol` and `Abraxius.Core` remain portable .NET libraries. They do not reference Avalonia, operating-system UI APIs, process implementations, shells, or agent/model frameworks.

`Abraxius.Platform` contains the platform-neutral contracts and the conservative current-environment detector. It owns `PlatformDescriptor`, `PlatformCapabilitySet`, `DeviceProfile`, `ExecutionBudget`, responsive viewport policy, platform-service interfaces, transport contracts, capability negotiation, and deterministic capability routing. It is still free of Avalonia.

`Abraxius.Platform.Desktop` is the desktop adapter boundary for direct process execution and filesystem access. `Abraxius.App` contains the shared Avalonia application, root view, workstation view model, and runtime bindings. It no longer owns a `Window`. `Abraxius.App.Desktop` owns the desktop lifetime and `Window`; Android, iOS, and Browser own their platform entry points. `Abraxius.App.Embedded` is an embedded Linux host seam that validates the environment without assuming a desktop window or framebuffer is present on the development machine.

## Capability discovery

The runtime asks the injected `IPlatformEnvironment` whether a capability is available. It does not ask whether the device is Windows, Android, or a browser. Availability is richer than a boolean:

```text
Available | Unavailable | PermissionRequired | Restricted | RemoteOnly
```

`PlatformEnvironmentFactory` reports the current runtime honestly. The implementation uses narrow runtime detection in the platform assembly only. Core scheduling code receives:

```text
ExecutionGraph + PlatformCapabilitySet + DeviceProfile + ExecutionBudget
```

and can make placement decisions without platform-name branches.

## Execution placement

`RuntimeExecutionMode` describes the role of a device:

```text
LocalFull          workstation/server/strong embedded target
LocalConstrained   sandboxed or resource-constrained local execution
Remote             client UI with execution hosted elsewhere
Hybrid             local capabilities plus remote fallback
```

`CapabilityResolver` first considers the local availability, then deterministic remote advertisements ordered by `RemoteHostId`. It returns a structured `CapabilityResolution` with local, remote, permission, restricted, or unavailable placement. The model is not involved in this deterministic decision.

Remote execution is identity-based. `ExecutionStateQuery` carries an `ExecutionId`, so a reconnecting client can resynchronize state independently of a particular transport connection. `IAbraxiusTransport` supports versioned messages, correlation IDs, streaming receive, cancellation, bounded loopback backpressure for tests, and explicit connection lifecycle states. Network authentication, multiplexed production transport, and reconnection policy are intentionally deferred to the distributed-runtime phase.

## Device and scheduling hints

`DeviceProfile` describes coarse resources and conditions without pretending to be a measurement system: device class, logical processors, approximate memory, battery/touch characteristics, graphics class, power source, memory pressure, and performance profile.

`ExecutionBudgetFactory` converts those hints and local capabilities into scheduler input. Browser and battery-constrained devices receive conservative CPU/model limits and prefer remote execution. A desktop with local model and Lattice capabilities can use a larger local budget. The Phase 4 scheduler consumes this budget directly; it does not rediscover operating-system facts.

The profiles are advisory. Actual queues, deadlines, rate limits, thermal events, and provider admission remain runtime concerns.

## Services and file references

Only adapters implement `IPlatformFileSystem`, `IProcessExecutionService`, secure storage, clipboard, notifications, URI opening, permissions, lifecycle, network information, and path-provider contracts. A process request is never a shell command by implication; the desktop adapter uses direct executable argument lists.

`PlatformFileReference` supports local user-granted paths, sandbox documents, browser files, remote artifacts, and Lattice resources. Higher layers therefore do not assume `C:\`, `/home`, `~/Documents`, or an unrestricted absolute path.

## Shared UI and responsive composition

The shared root is `MainView`, not `Window`. The desktop host attaches it to a desktop window; mobile and browser hosts can attach it to their own top-level lifetime. `ViewportProfile` and `ResponsiveLayoutPolicy` use logical viewport size, scale, touch, and reduced-motion state. They do not use product forks such as “iPhone layout” or “Windows layout”.

The existing desktop information-dense view remains shared. Compact layouts can collapse secondary panels and show bottom navigation; expanded layouts show the inspector and event surfaces. Runtime events still flow through the existing bounded/coalesced UI state path rather than directly into a visual tree.

## AOT and dependency policy

The new contracts use explicit registrations, immutable records, typed IDs, and source-visible factories. They do not require runtime assembly scanning, dynamic code generation, or arbitrary `Activator` calls. This keeps trimming and future AOT work practical without making experimental platform-specific Native AOT paths a prerequisite.

Phase 1/2 package dependencies were audited at the project boundary. Protocol, Core, and Platform have no Avalonia or mobile/native UI references. Avalonia is confined to shared App and the platform host packages. Desktop process APIs are confined to `Abraxius.Platform.Desktop`.

## Current validation state

The current development environment is Linux Mint 22.3 x64 with .NET SDK 10.0.302 and no installed .NET workloads. The following projects compile on this machine:

```text
Protocol, Core, Platform, Platform.Desktop, Runtime,
App, App.Desktop, App.Embedded, CLI,
and the platform-neutral test projects
```

Android and iOS host projects are present with exact Avalonia 12.1.1 package references, but their target workloads are not installed here. They are architecturally established, not claimed as locally compiled. The Browser host also uses Avalonia 12.1.1 and compiled successfully on this machine; no browser runtime smoke test was run from this shell. iOS device/publish validation additionally requires the Apple toolchain and a macOS runner. Embedded framebuffer/DRM execution is not claimed: the host seam compiles, but this workstation is not an embedded target.

The intended CI matrix is:

| Target | Validation | Runner/toolchain |
| --- | --- | --- |
| `net10.0` shared/core/runtime | build and unit tests | Linux, Windows, macOS |
| Desktop Avalonia | compile and smoke startup | Linux, Windows, macOS |
| `net10.0-android` | compile, emulator smoke | Android workload + Android SDK |
| `net10.0-ios` | compile, simulator/device smoke | macOS + Xcode + iOS workload |
| Browser/WASM | compile and browser smoke | WASM workload/browser runner |
| Embedded Linux | compile, target-device smoke | embedded Linux framebuffer/DRM image |

“Architecturally supported”, “compiled”, “emulator tested”, and “physical-device tested” are separate claims and must remain separate in release reporting.
