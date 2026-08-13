# Update architecture

`IUpdateService` is the provider-neutral contract. The current desktop implementation is
`VelopackUpdateService`, which is the only assembly that references Velopack. It uses the official
GitHub Releases source, an explicit Stable/Beta/Development channel, package metadata, and
Velopack's download/apply/restart APIs.

```text
background check
      ↓
compatible UpdateInfo
      ↓
bounded background download
      ↓
integrity/package verification by updater
      ↓
UpdateCoordinator
      ↓
runtime checkpoint + graceful shutdown
      ↓
Velopack apply/restart
      ↓
startup health marker
```

Developer and portable builds report `NotInstalled`/`Unavailable` and never replace the working
directory. Checks are delayed and periodic; startup remains usable when GitHub is offline. Release
notes are treated as untrusted presentation data and are not executed.

The updater rejects non-increasing versions, wrong channels, and unsupported installation kinds.
Downloaded update state is held by one service instance; CLI `download` and `apply` therefore run
in one process. The default UI uses restart-now only after the user invokes the update action;
apply-on-exit is available through the coordinator for a host that can provide a shutdown callback.
