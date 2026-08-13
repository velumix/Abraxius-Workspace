# Linux installation

The direct Linux artifact is a Velopack AppImage. A managed install copies it to the stable user
path selected by `LinuxInstallationIntegration` (by default
`~/.local/share/Abraxius/Abraxius.AppImage`) and writes an owned
`com.abraxius.Abraxius.desktop` entry under the XDG data directory. The entry targets the stable
path, not a versioned file in Downloads, and registers `abraxius://` through the desktop entry.

The integration is idempotent and refuses to overwrite an existing unowned desktop entry. Remove
deletes only the managed entry, never the AppImage or user data. Portable mode performs no
integration. Package-manager installs are a separate ownership mode and must update from their
package source.

`packaging/install-linux.sh` copies a selected AppImage to the stable managed path. The first
managed launch reconciles the desktop entry; a `--portable` install performs neither action.

The AppImage package, Velopack release metadata, and SHA-256 manifest were built and checked on
the current Linux host. A desktop-environment launch test still requires a graphical clean-machine
run; the repository does not claim that physical XDG menu testing has occurred here.
