# Distribution architecture

Abraxius has one product version source (`build/Version.props`) and platform-native installation
owners. The desktop direct-distribution path is Velopack 1.2.0; its GitHub Releases feed is
wrapped by `IUpdateService`, so Core and the Avalonia UI do not depend on Velopack types.

```text
canonical version
      ↓
dotnet publish + vpk
      ↓
signed/attested GitHub Release
      ↓
VelopackUpdateService → UpdateCoordinator → shutdown/checkpoint → apply/restart
      ↓
startup health marker
```

Persistent configuration, ledger, evidence, caches, and models are owned by the Phase 3 path
provider and live outside replaceable application binaries. Linux direct installs use a stable
per-user AppImage path and an owned XDG desktop entry. Portable AppImages do not create desktop
integration. Package-manager, App Store, and Play Store installs are owned by their store and do
not use the GitHub self-updater.

The release workflow builds desktop RIDs on native runners, publishes a browser artifact, generates
checksums and a machine-readable manifest, and creates a draft release before publication. Signing
and notarization are protected CI steps and require credentials; local builds remain intentionally
unsigned and are not production releases.
