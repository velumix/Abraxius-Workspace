# macOS installation

macOS direct distribution is a native Velopack package containing the Avalonia `.app` bundle.
Production artifacts must be Developer ID signed and notarized before publication. The identity,
notarization credentials, and keychain profile are CI secrets; they are never committed.

The application should live as a normal macOS application and does not force a Dock pin. Velopack
owns direct-install update replacement. App Store builds use App Store updates instead and are not
self-updated from GitHub.

The protected macOS runner must validate install, Gatekeeper/signature state, update/restart, and
relaunch. macOS packaging and notarization are not physically validated in this Linux workspace.
