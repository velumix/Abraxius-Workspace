# PluginHost

`Abraxius.PluginHost` is a CoreCLR headless process. It verifies the approved package hash, loads one explicit generated entrypoint in a collectible load context, initializes under a bounded timeout, and exposes typed invocation and health operations. Cooperative unload is attempted; terminating the host is the correctness fallback. Crash-loop policy quarantines after repeated failures instead of restarting forever.
