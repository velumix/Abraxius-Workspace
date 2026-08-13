# Activation

`IActivationRouter` accepts typed tray, notification, deep-link, file, and startup requests. Only registered action IDs and strict `abraxius://` routes are accepted. Supported routes identify home, mission, Needs You, Skill, and update settings surfaces.

Activation data is untrusted. Unknown actions, foreign URI schemes, arbitrary payloads, AXL, and shell commands are rejected. A successful desktop activation restores and focuses the existing workstation window, then selects the exact relevant surface.
