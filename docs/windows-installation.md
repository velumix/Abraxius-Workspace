# Windows installation

Windows direct distribution is a per-user Velopack `Setup.exe` package. Velopack owns the stable
launcher and ordinary Desktop/Start Menu shortcuts, so shortcuts do not point into a versioned
application directory. Optional safe-mode and diagnostics shortcuts are semantic Abraxius
specifications; they are not installed by default.

Production binaries and installers must be Authenticode-signed in protected CI. No signing key is
stored in this repository. Launch-at-login is opt-in and taskbar pinning is never forced.

The clean-machine acceptance test is:

1. install Setup.exe;
2. launch from Desktop and Start Menu;
3. update from a real previous release;
4. launch both shortcuts again and verify the new `BuildInfo` version;
5. uninstall and verify managed shortcuts are removed while user data remains.

This Linux workspace cannot claim that Windows installation was physically executed. The native
runner workflow and signing hook are present for that validation.
