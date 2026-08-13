# Rollback and recovery

Before an update restart, the host must flush runtime/ledger state, stop optional sidecars, end
voice sessions, persist layout, and close active work according to its normal shutdown policy.
`StartupHealthMarker` writes an unconfirmed startup record before launching and marks the build
healthy only after core startup completes. Repeated unconfirmed starts expose `RecoveryRequired`
for a recovery UI/launcher.

Safe mode is represented by the stable `--safe-mode` launch identity and is intended to disable
optional plugins, sidecars, custom layouts, and large local models while leaving update/diagnostic
paths available. `abraxius update rollback` is an explicit recovery action: it selects a retained
older full package, verifies its checksum through Velopack, and asks the updater to apply it with
downgrade permission. It never runs automatically from model output or release-note prose.

This boundary prevents a failed startup from corrupting the installation or silently accepting an
older release. The current test suite validates startup-marker transitions, anti-downgrade checks,
and update state behavior; a clean-machine crash-before-health rollback remains a hosted-runner
acceptance test.
