# Platform compatibility matrix

The matrix describes the implemented architecture and the capabilities a host may advertise. A check mark in the UI column means the shared Avalonia application has a host path; it does not mean a physical device has been tested.

| Target | Shared UI host | Default mode | Local process/files | Local model/Lattice | Remote fallback | Validation on this machine |
| --- | --- | --- | --- | --- | --- | --- |
| Windows | Desktop host | LocalFull | Host capability | Host capability | Yes | Not run; Linux host |
| Linux | Desktop host | LocalFull | Host capability | Host capability | Yes | Shared and desktop projects compiled |
| macOS | Desktop host | LocalFull | Host capability | Host capability | Yes | Not run; macOS/Xcode runner required for Apple targets |
| Android ARM64 | Android host | Hybrid | Sandbox/permission dependent | Optional/restricted | Yes | Host source present; Android workload unavailable |
| iOS ARM64 | iOS host | Hybrid | Sandbox/permission dependent | Optional/restricted | Yes | Host source present; iOS workload and Apple toolchain unavailable |
| Browser/WASM | Browser host | Remote | Unavailable by default | Remote | Yes | Browser host compiled; no browser runtime smoke |
| Embedded Linux | Embedded host seam | LocalConstrained | Target capability | Target capability | Yes | Host seam compiled; no embedded framebuffer/DRM target attached |

## Capability states

Capabilities are advertised with a `CapabilityId`, availability state, optional version, and constraints. `RemoteOnly` is intentionally different from `Unavailable`: it tells the resolver that a capability is expected to be supplied by a remote host. Permission and restriction errors remain structured so UI and policy layers can explain them without leaking implementation exceptions.

## Architecture coverage

Both x64 and ARM64 are represented by `PlatformDescriptor.Architecture`. Operating-system family and CPU architecture are independent. The shared contracts do not assume a desktop process, unrestricted filesystem, shell, GPU API, window, or network.

## Build status vocabulary

* **Architecturally established**: project/contracts exist and fit the dependency rules.
* **Compiled**: the target was built successfully on the current machine.
* **Emulator tested**: a platform emulator/simulator smoke test passed.
* **Physical-device tested**: hardware validation passed.

Phase 3 currently reports the first two categories only for the Linux-valid targets; no emulator or physical-device testing is claimed.
